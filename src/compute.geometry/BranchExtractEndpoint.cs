using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;

namespace compute.geometry
{
    // POST /branch-extract is only a supervisor. Rhino and Branch model loading
    // belong exclusively to the one-model branch.extract.exe child process.
    public static class BranchExtractModule
    {
        const int DefaultTimeoutMilliseconds = 300_000;
        const int TimeoutExitGraceMilliseconds = 5_000;
        const int MaximumModelsPerRequest = 40;
        const long DefaultMaximumOutputBytes = 50L * 1024L * 1024L;
        const string SchemaVersion = "1.0";

        static readonly object s_extractLock = new object();
        static readonly object s_initializationLock = new object();
        static readonly HashSet<string> s_statuses = new HashSet<string>(StringComparer.Ordinal)
        {
            "ok",
            "no_branch_content",
            "needs_upgrade",
            "timeout",
            "plugin_load_failed",
            "partial",
            "partial_invalid",
            "partial_truncated",
            "crashed"
        };

        static bool s_initialized;
        static string s_executablePath;
        static string s_tempDirectory;
        static int s_defaultTimeoutMilliseconds;
        static long s_maximumOutputBytes;
        static string s_initializationFailureStatus;
        static string s_initializationFailureDetail;

        // Called from Startup.Configure before Rhino is initialized. A missing
        // executable is a deployment error and must never activate the retired
        // in-process dictionary extraction path.
        public static void Initialize()
        {
            lock (s_initializationLock)
            {
                if (s_initialized)
                    return;

                string executableOverride =
                    Environment.GetEnvironmentVariable("BRANCH_EXTRACT_EXE_PATH");
                if (String.IsNullOrWhiteSpace(executableOverride))
                {
                    executableOverride =
                        Environment.GetEnvironmentVariable("BRANCH_EXTRACT_EXE");
                }
                string executablePath = null;
                int defaultTimeoutMilliseconds = ReadConfiguredTimeoutMilliseconds();
                long maximumOutputBytes = ReadConfiguredMaximumOutputBytes();
                string tempDirectory =
                    Path.Combine(Path.GetTempPath(), "branch-extract-supervisor");

                try
                {
                    Directory.CreateDirectory(tempDirectory);
                    SweepTempDirectory(tempDirectory);
                }
                catch (Exception ex)
                {
                    s_initializationFailureStatus = "crashed";
                    s_initializationFailureDetail =
                        $"branch_extract_temp_failed: unable to create and sweep '{tempDirectory}': {ex.Message}";
                    Log.Error(ex, "{Message}", s_initializationFailureDetail);
                }

                try
                {
                    executablePath = String.IsNullOrWhiteSpace(executableOverride)
                        ? Path.Combine(AppContext.BaseDirectory, "branch.extract.exe")
                        : ResolveOverridePath(executableOverride);
                    if (!File.Exists(executablePath))
                    {
                        throw new FileNotFoundException(
                            "Branch extractor executable was not found.",
                            executablePath);
                    }

                    using var executable = new FileStream(
                        executablePath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    if (executable.Length == 0)
                        throw new InvalidDataException("Branch extractor executable is empty.");
                }
                catch (Exception ex)
                {
                    s_initializationFailureStatus = "plugin_load_failed";
                    s_initializationFailureDetail =
                        $"plugin_load_failed: branch.extract.exe is unavailable or inaccessible at " +
                        $"'{executablePath ?? executableOverride ?? "<default>"}': {ex.Message}";
                    Log.Error(ex, "{Message}", s_initializationFailureDetail);
                }

                s_executablePath = executablePath;
                s_tempDirectory = tempDirectory;
                s_defaultTimeoutMilliseconds = defaultTimeoutMilliseconds;
                s_maximumOutputBytes = maximumOutputBytes;
                s_initialized = true;

                if (s_initializationFailureStatus == null)
                {
                    Log.Information(
                        "Branch extractor supervisor initialized with executable {ExecutablePath}, " +
                        "default timeout {TimeoutMilliseconds} ms, and maximum output {MaximumOutputBytes} bytes",
                        s_executablePath,
                        s_defaultTimeoutMilliseconds,
                        s_maximumOutputBytes);
                }
            }
        }

        public static void MapEndpoints(IEndpointRouteBuilder app)
        {
            app.MapPost("/branch-extract", Extract);
        }

        // The endpoint always returns HTTP 200. Request validation, individual
        // child failures, and unexpected supervisor failures are represented in
        // the JSON response so a partial batch is never mistaken for total loss.
        public static async Task Extract(HttpContext context)
        {
            context.Response.ContentType = "application/json";
            context.Response.StatusCode = StatusCodes.Status200OK;

            BranchExtractRequest request;
            try
            {
                request = await ReadRequest(context);
            }
            catch (RequestValidationException ex)
            {
                await context.Response.WriteAsync(
                    CreateRequestFailure(ex.Message).ToString(Formatting.None));
                return;
            }
            catch (JsonException ex)
            {
                await context.Response.WriteAsync(
                    CreateRequestFailure(
                        $"invalid_request: request body is not valid JSON: {ex.Message}")
                    .ToString(Formatting.None));
                return;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Unable to validate branch extraction request");
                await context.Response.WriteAsync(
                    CreateRequestFailure(
                        $"invalid_request: request could not be validated: {ex.Message}")
                    .ToString(Formatting.None));
                return;
            }

            JObject batch;
            try
            {
                batch = await Task.Run(() => ExtractBatch(request));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Unexpected branch extractor supervisor failure");
                batch = CreateBatchFailure(
                    request.Models,
                    "supervisor_failed",
                    ex.Message);
            }

            await context.Response.WriteAsync(batch.ToString(Formatting.None));
        }

        static async Task<BranchExtractRequest> ReadRequest(HttpContext context)
        {
            string bodyText;
            using (var reader = new StreamReader(
                context.Request.Body,
                Encoding.UTF8,
                true,
                4096,
                true))
            {
                bodyText = await reader.ReadToEndAsync();
            }

            JToken parsed = JToken.Parse(bodyText);

            // Three request shapes are accepted, deliberately:
            //   [ "a.3dm", "b.3dm" ]        the historical pre-1.0 form
            //   { "paths":  [ ... ] }       what the live Python client sends
            //                               (Masterlists/Python/extract_branch_data.py)
            //   { "models": [ ... ] }       also honoured
            // "paths" is the established external contract (plan R9). Accepting only
            // "models" broke every existing caller with an unhelpful 200 + empty results,
            // which is exactly how a wire-format mismatch hides in plain sight.
            JObject body = parsed as JObject ?? new JObject();
            JArray modelTokens =
                parsed as JArray
                ?? body["paths"] as JArray
                ?? body["models"] as JArray;

            if (modelTokens == null || modelTokens.Count == 0)
            {
                throw new RequestValidationException(
                    "invalid_request: expected a non-empty array of model paths — either a bare JSON array, or under \"paths\".");
            }
            if (modelTokens.Count > MaximumModelsPerRequest)
            {
                throw new RequestValidationException(
                    $"invalid_request: models accepts at most {MaximumModelsPerRequest} entries.");
            }

            int? timeoutMilliseconds = null;
            JToken timeoutToken = body["timeout_ms"];
            if (timeoutToken != null && timeoutToken.Type != JTokenType.Null)
            {
                if (timeoutToken.Type != JTokenType.Integer)
                {
                    throw new RequestValidationException(
                        "invalid_request: timeout_ms must be a positive 32-bit integer.");
                }

                long configuredTimeout = timeoutToken.Value<long>();
                if (configuredTimeout <= 0 || configuredTimeout > Int32.MaxValue)
                {
                    throw new RequestValidationException(
                        "invalid_request: timeout_ms must be a positive 32-bit integer.");
                }

                timeoutMilliseconds = (int)configuredTimeout;
            }

            var models = new List<BranchExtractModel>(modelTokens.Count);
            for (int index = 0; index < modelTokens.Count; index++)
            {
                // A entry may be a bare path string — which is what the live Python client
                // sends, `{"paths": ["a.3dm", "b.3dm"]}` — or an object carrying a "path"
                // plus optional metadata such as "job". Normalise the string form into the
                // object form so the rest of this loop has one shape to reason about.
                JToken rawEntry = modelTokens[index];
                if (rawEntry?.Type == JTokenType.String)
                {
                    rawEntry = new JObject { ["path"] = rawEntry.Value<string>() };
                }

                if (!(rawEntry is JObject modelToken))
                {
                    throw new RequestValidationException(
                        $"invalid_request: models[{index}] must be a path string or an object with a \"path\" property.");
                }

                JToken pathToken = modelToken["path"];
                if (pathToken?.Type != JTokenType.String ||
                    String.IsNullOrWhiteSpace(pathToken.Value<string>()))
                {
                    throw new RequestValidationException(
                        $"invalid_request: models[{index}].path must be a non-empty string.");
                }

                JToken jobToken = modelToken["job"];
                if (jobToken != null &&
                    jobToken.Type != JTokenType.Null &&
                    jobToken.Type != JTokenType.String)
                {
                    throw new RequestValidationException(
                        $"invalid_request: models[{index}].job must be a string or null.");
                }

                string requestedPath = pathToken.Value<string>();
                string resolvedPath;
                string validationFailure = null;
                try
                {
                    resolvedPath = Path.GetFullPath(requestedPath);
                    if (!File.Exists(resolvedPath))
                    {
                        validationFailure =
                            $"model_not_found: model file does not exist at '{resolvedPath}'.";
                    }
                }
                catch (Exception ex) when (
                    ex is ArgumentException ||
                    ex is NotSupportedException ||
                    ex is IOException ||
                    ex is System.Security.SecurityException)
                {
                    resolvedPath = requestedPath;
                    validationFailure =
                        $"model_path_invalid: '{requestedPath}' is not a valid model path: {ex.Message}";
                }

                models.Add(new BranchExtractModel
                {
                    Job = jobToken?.Type == JTokenType.String
                        ? jobToken.Value<string>()
                        : null,
                    RequestedPath = requestedPath,
                    ResolvedPath = resolvedPath,
                    ValidationFailure = validationFailure
                });
            }

            return new BranchExtractRequest
            {
                Models = models,
                TimeoutMilliseconds = timeoutMilliseconds
            };
        }

        static JObject ExtractBatch(BranchExtractRequest request)
        {
            EnsureInitialized();

            int timeoutMilliseconds =
                request.TimeoutMilliseconds ?? s_defaultTimeoutMilliseconds;
            var results = new JArray();
            bool anyFailures = false;
            string pluginFailureDetail = null;

            // Holding this lock for the whole batch prevents concurrent requests
            // from interleaving children. Each batch itself also starts and waits
            // for exactly one child at a time.
            lock (s_extractLock)
            {
                foreach (BranchExtractModel model in request.Models)
                {
                    JObject result;
                    if (s_initializationFailureStatus != null)
                    {
                        result = CreateModelResult(
                            model,
                            s_initializationFailureStatus,
                            s_initializationFailureDetail,
                            null,
                            null,
                            null,
                            null,
                            0);
                    }
                    else if (model.ValidationFailure != null)
                    {
                        result = CreateModelResult(
                            model,
                            "crashed",
                            model.ValidationFailure,
                            null,
                            null,
                            null,
                            null,
                            0);
                    }
                    else if (pluginFailureDetail != null)
                    {
                        result = CreateModelResult(
                            model,
                            "plugin_load_failed",
                            "plugin_load_failed: model was not started because an earlier " +
                            $"child could not load. {pluginFailureDetail}",
                            null,
                            null,
                            null,
                            null,
                            0);
                    }
                    else
                    {
                        try
                        {
                            result = ExtractModel(model, timeoutMilliseconds);
                        }
                        catch (Exception ex)
                        {
                            Log.Error(
                                ex,
                                "Unexpected supervisor error for branch model {ModelPath}",
                                model.RequestedPath);
                            result = CreateModelResult(
                                model,
                                "crashed",
                                $"supervisor_model_failed: {ex.Message}",
                                null,
                                null,
                                null,
                                null,
                                0);
                        }
                    }

                    string status = result.Value<string>("status");
                    if (!String.Equals(status, "ok", StringComparison.Ordinal))
                        anyFailures = true;

                    if (String.Equals(
                        status,
                        "plugin_load_failed",
                        StringComparison.Ordinal))
                    {
                        pluginFailureDetail =
                            result.Value<string>("status_detail");
                    }

                    results.Add(result);
                }
            }

            return new JObject
            {
                ["any_failures"] = anyFailures,
                ["results"] = results
            };
        }

        static JObject ExtractModel(
            BranchExtractModel model,
            int timeoutMilliseconds)
        {
            string modelHash = HashModelPath(model.ResolvedPath);
            string outputPath = Path.Combine(
                s_tempDirectory,
                $"branch-extract-{Environment.ProcessId}-{modelHash}-{Guid.NewGuid():N}.json");

            JObject result;
            var stopwatch = Stopwatch.StartNew();
            string cleanupFailure = null;

            try
            {
                result = RunChild(
                    model,
                    outputPath,
                    timeoutMilliseconds,
                    stopwatch);
            }
            catch (Exception ex)
            {
                result = CreateModelResult(
                    model,
                    "crashed",
                    $"supervisor_model_failed: {ex.Message}",
                    null,
                    null,
                    null,
                    null,
                    stopwatch.ElapsedMilliseconds);
            }
            finally
            {
                stopwatch.Stop();
                cleanupFailure = DeleteModelTempFiles(outputPath);
            }

            result["elapsed_ms"] = stopwatch.ElapsedMilliseconds;
            if (cleanupFailure != null)
            {
                AppendStatusDetail(
                    result,
                    $"temp_delete_failed: {cleanupFailure}");
                if (String.Equals(
                    result.Value<string>("status"),
                    "ok",
                    StringComparison.Ordinal))
                {
                    result["status"] = "crashed";
                }
            }

            return result;
        }

        static JObject RunChild(
            BranchExtractModel model,
            string outputPath,
            int timeoutMilliseconds,
            Stopwatch stopwatch)
        {
            if (!File.Exists(s_executablePath))
            {
                return CreateModelResult(
                    model,
                    "plugin_load_failed",
                    "plugin_load_failed: branch extractor executable disappeared " +
                    $"from '{s_executablePath}'.",
                    null,
                    null,
                    null,
                    null,
                    stopwatch.ElapsedMilliseconds);
            }

            if (!File.Exists(model.ResolvedPath))
            {
                return CreateModelResult(
                    model,
                    "crashed",
                    $"model_not_found: model file disappeared from '{model.ResolvedPath}'.",
                    null,
                    null,
                    null,
                    null,
                    stopwatch.ElapsedMilliseconds);
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = s_executablePath,
                WorkingDirectory =
                    Path.GetDirectoryName(s_executablePath) ??
                    AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("--model");
            startInfo.ArgumentList.Add(model.ResolvedPath);
            startInfo.ArgumentList.Add("--out");
            startInfo.ArgumentList.Add(outputPath);
            startInfo.ArgumentList.Add("--timeout-ms");
            startInfo.ArgumentList.Add(
                timeoutMilliseconds.ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add($"-childof:{Environment.ProcessId}");

            using (var child = new Process { StartInfo = startInfo })
            {
                try
                {
                    if (!child.Start())
                    {
                        return CreateModelResult(
                            model,
                            "plugin_load_failed",
                            "plugin_load_failed: Process.Start returned false.",
                            null,
                            null,
                            null,
                            null,
                            stopwatch.ElapsedMilliseconds);
                    }
                }
                catch (Exception ex)
                {
                    return CreateModelResult(
                        model,
                        "plugin_load_failed",
                        "plugin_load_failed: branch extractor could not be " +
                        $"started: {ex.Message}",
                        null,
                        null,
                        null,
                        null,
                        stopwatch.ElapsedMilliseconds);
                }

                Task<string> stdoutTask =
                    child.StandardOutput.ReadToEndAsync();
                Task<string> stderrTask =
                    child.StandardError.ReadToEndAsync();

                bool exited;
                try
                {
                    int supervisorWaitMilliseconds =
                        timeoutMilliseconds >
                        Int32.MaxValue - TimeoutExitGraceMilliseconds
                            ? Int32.MaxValue
                            : timeoutMilliseconds +
                                TimeoutExitGraceMilliseconds;
                    exited = child.WaitForExit(supervisorWaitMilliseconds);
                }
                catch (Exception ex)
                {
                    string killFailure = KillChild(child);
                    return CreateModelResult(
                        model,
                        "crashed",
                        CombineDetails(
                            $"process_wait_failed: {ex.Message}",
                            killFailure == null
                                ? null
                                : $"child_kill_failed: {killFailure}"),
                        null,
                        TryGetExitCode(child),
                        ReadCapturedStream(stderrTask, "stderr"),
                        ReadCapturedStream(stdoutTask, "stdout"),
                        stopwatch.ElapsedMilliseconds);
                }

                if (!exited)
                {
                    // The worker has its own watchdog so it can distinguish a
                    // pre-BranchDoc upgrade hang from a later traversal timeout.
                    // The wait above includes a five-second write/exit buffer
                    // after that watchdog's deadline. Enforce termination now.
                    string killFailure = KillChild(child);
                    string stderr =
                        ReadCapturedStream(stderrTask, "stderr");
                    string stdout =
                        ReadCapturedStream(stdoutTask, "stdout");

                    JObject childData = null;
                    string childOutputFailure = null;
                    if (File.Exists(outputPath))
                    {
                        try
                        {
                            childData = ReadChildOutput(outputPath);
                        }
                        catch (ChildOutputException ex)
                        {
                            childOutputFailure =
                                $"{ex.Code}: {ex.Message}";
                        }
                    }

                    string childStatus = GetChildStatus(childData);
                    string status = childOutputFailure != null &&
                        childOutputFailure.StartsWith(
                            "output_too_large:",
                            StringComparison.Ordinal)
                            ? "crashed"
                            : childStatus == "plugin_load_failed"
                                ? "plugin_load_failed"
                                : childStatus == "needs_upgrade" ||
                                    childStatus == "timeout"
                                    ? childStatus
                                    : IndicatesPluginLoadFailure(stderr)
                                        ? "plugin_load_failed"
                                        : IndicatesPreBranchDocumentTimeout(stderr)
                                            ? "needs_upgrade"
                                            : "timeout";
                    string statusDetail =
                        GetChildStatusDetail(childData);
                    if (String.IsNullOrWhiteSpace(statusDetail))
                    {
                        statusDetail = status == "crashed" &&
                            childOutputFailure?.StartsWith(
                                "output_too_large:",
                                StringComparison.Ordinal) == true
                            ? "output_too_large: child output exceeded the size budget."
                            : status == "plugin_load_failed"
                                ? "plugin_load_failed: Branch could not be loaded."
                                : status == "needs_upgrade"
                                    ? "needs_upgrade: child timed out before BranchDoc materialized."
                                    : $"timeout: child exceeded the {timeoutMilliseconds} ms " +
                                        $"per-model timeout plus the {TimeoutExitGraceMilliseconds} ms " +
                                        "watchdog write/exit buffer.";
                    }

                    statusDetail = CombineDetails(
                        statusDetail,
                        childOutputFailure,
                        killFailure == null
                            ? null
                            : $"child_kill_failed: {killFailure}");

                    return CreateModelResult(
                        model,
                        status,
                        statusDetail,
                        childData,
                        TryGetExitCode(child),
                        stderr,
                        stdout,
                        stopwatch.ElapsedMilliseconds);
                }

                string childStdout =
                    ReadCapturedStream(stdoutTask, "stdout");
                string childStderr =
                    ReadCapturedStream(stderrTask, "stderr");
                int exitCode = child.ExitCode;

                JObject data = null;
                ChildOutputException outputFailure = null;
                try
                {
                    data = ReadChildOutput(outputPath);
                }
                catch (ChildOutputException ex)
                {
                    outputFailure = ex;
                }

                if (outputFailure?.Code == "output_too_large")
                {
                    return CreateModelResult(
                        model,
                        "crashed",
                        $"{outputFailure.Code}: {outputFailure.Message}",
                        null,
                        exitCode,
                        childStderr,
                        childStdout,
                        stopwatch.ElapsedMilliseconds);
                }

                if (exitCode != 0)
                {
                    string status =
                        GetChildStatus(data) == "plugin_load_failed" ||
                        IndicatesPluginLoadFailure(childStderr)
                            ? "plugin_load_failed"
                            : "crashed";
                    return CreateModelResult(
                        model,
                        status,
                        CombineDetails(
                            $"{status}: child exited with code {exitCode}.",
                            outputFailure == null
                                ? null
                                : $"{outputFailure.Code}: {outputFailure.Message}"),
                        data,
                        exitCode,
                        childStderr,
                        childStdout,
                        stopwatch.ElapsedMilliseconds);
                }

                if (outputFailure != null)
                {
                    string status = IndicatesPluginLoadFailure(childStderr)
                        ? "plugin_load_failed"
                        : "crashed";
                    return CreateModelResult(
                        model,
                        status,
                        $"{outputFailure.Code}: {outputFailure.Message}",
                        null,
                        exitCode,
                        childStderr,
                        childStdout,
                        stopwatch.ElapsedMilliseconds);
                }

                return CreateModelResult(
                    model,
                    GetChildStatus(data),
                    GetChildStatusDetail(data),
                    data,
                    exitCode,
                    childStderr,
                    childStdout,
                    stopwatch.ElapsedMilliseconds);
            }
        }

        static JObject ReadChildOutput(string outputPath)
        {
            if (!File.Exists(outputPath))
            {
                throw new ChildOutputException(
                    "output_missing",
                    "Child exited without writing its --out file.");
            }

            byte[] bytes;
            try
            {
                using var stream = new FileStream(
                    outputPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read | FileShare.Delete);
                long reportedLength = stream.Length;
                if (reportedLength > s_maximumOutputBytes ||
                    reportedLength > Int32.MaxValue)
                {
                    throw new ChildOutputException(
                        "output_too_large",
                        $"Child output is {reportedLength} bytes; the per-model " +
                        $"limit is {s_maximumOutputBytes} bytes. The file was not read.");
                }

                bytes = new byte[(int)reportedLength];
                stream.ReadExactly(bytes);
            }
            catch (ChildOutputException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new ChildOutputException(
                    "output_read_failed",
                    ex.Message,
                    ex);
            }

            string json;
            try
            {
                int offset =
                    bytes.Length >= 3 &&
                    bytes[0] == 0xEF &&
                    bytes[1] == 0xBB &&
                    bytes[2] == 0xBF
                        ? 3
                        : 0;
                json = new UTF8Encoding(false, true)
                    .GetString(bytes, offset, bytes.Length - offset);
            }
            catch (Exception ex)
            {
                throw new ChildOutputException(
                    "output_invalid_utf8",
                    ex.Message,
                    ex);
            }

            JObject data;
            ExtractionRecord record;
            try
            {
                using var stringReader = new StringReader(json);
                using var jsonReader = new JsonTextReader(stringReader)
                {
                    DateParseHandling = DateParseHandling.None
                };
                data = JObject.Load(jsonReader);
                if (jsonReader.Read())
                {
                    throw new JsonSerializationException(
                        "Child output contains data after the root JSON object.");
                }
                record = data.ToObject<ExtractionRecord>();
            }
            catch (JsonException ex)
            {
                throw new ChildOutputException(
                    "output_invalid_json",
                    ex.Message,
                    ex);
            }

            if (record == null)
            {
                throw new ChildOutputException(
                    "output_contract_invalid",
                    "Child output did not deserialize to an extraction record.");
            }

            ValidateSchemaEnvelope(data);

            if (!String.Equals(
                record.SchemaVersion,
                SchemaVersion,
                StringComparison.Ordinal))
            {
                throw new ChildOutputException(
                    "output_contract_invalid",
                    $"Expected schema_version '{SchemaVersion}', received " +
                    $"'{record.SchemaVersion ?? "<missing>"}'.");
            }

            string status = record.Extraction?.Status;
            if (status == null || !s_statuses.Contains(status))
            {
                throw new ChildOutputException(
                    "output_contract_invalid",
                    "Child output has an unknown extraction.status " +
                    $"'{status ?? "<missing>"}'.");
            }

            if (String.Equals(status, "ok", StringComparison.Ordinal))
            {
                bool hasFieldErrors = data
                    .Descendants()
                    .OfType<JProperty>()
                    .Where(property => property.Name == "field_errors")
                    .Any(property =>
                        property.Value is JObject errors &&
                        errors.HasValues);
                int panelsWithErrors =
                    data.SelectToken("summary.panels_with_errors")?
                        .Value<int>() ?? 0;
                if (hasFieldErrors || panelsWithErrors != 0)
                {
                    throw new ChildOutputException(
                        "output_contract_invalid",
                        "extraction.status cannot be ok when field_errors " +
                        "or summary.panels_with_errors are non-empty.");
                }
            }

            if (String.Equals(
                status,
                "partial_truncated",
                StringComparison.Ordinal))
            {
                ValidateDroppedRecord(data, record.Extraction.Dropped);
            }

            return data;
        }

        // Newtonsoft.Json.Schema is not a dependency of compute.geometry. Validate
        // the frozen schema's complete envelope and the required identity fields of
        // every row here, before any child document is allowed into the aggregate.
        static void ValidateSchemaEnvelope(JObject data)
        {
            RequireAllowedProperties(
                data,
                "$",
                "schema_version", "generated_at", "snapshot_date", "job", "job_name",
                "product_type", "product_subtype", "source", "extraction", "lod",
                "panels", "elements", "summary", "units");
            RequireString(data, "schema_version", "$");
            RequireString(data, "generated_at", "$");
            RequireString(data, "snapshot_date", "$");
            RequireString(data, "job", "$");
            OptionalStringOrNull(data, "job_name", "$");
            OptionalStringOrNull(data, "product_subtype", "$");
            if (!DateTimeOffset.TryParse(
                    data.Value<string>("generated_at"),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out _))
            {
                ThrowContract(
                    "$.generated_at must be an ISO-8601 date-time.");
            }
            if (!DateTime.TryParseExact(
                    data.Value<string>("snapshot_date"),
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out _))
            {
                ThrowContract("$.snapshot_date must use yyyy-MM-dd.");
            }

            if (data.TryGetValue("product_type", out JToken productType) &&
                (productType.Type != JTokenType.String ||
                 productType.Value<string>() != "DLT"))
            {
                ThrowContract("$.product_type must equal \"DLT\" when present.");
            }

            JObject source = RequireObject(data, "source", "$");
            RequireAllowedProperties(
                source,
                "$.source",
                "extractor", "extractor_version", "branch_plugin_version",
                "rhino_version", "model_file", "model_revision",
                "branch_project_guid", "weight_source",
                "complexity_config_version");
            RequireString(source, "extractor", "$.source");
            RequireString(source, "model_file", "$.source");
            RequireString(source, "weight_source", "$.source");
            foreach (string name in new[]
            {
                "extractor_version", "branch_plugin_version", "rhino_version",
                "model_revision", "branch_project_guid",
                "complexity_config_version"
            })
            {
                OptionalStringOrNull(source, name, "$.source");
            }
            if (source.Value<string>("weight_source") !=
                "branch_native_getweight")
            {
                ThrowContract(
                    "$.source.weight_source must equal \"branch_native_getweight\".");
            }

            JObject extraction = RequireObject(data, "extraction", "$");
            RequireAllowedProperties(
                extraction,
                "$.extraction",
                "status", "status_detail", "types_recognised",
                "types_unrecognised", "blocks", "dropped", "excluded",
                "elapsed_ms");
            RequireString(extraction, "status", "$.extraction");
            OptionalStringOrNull(extraction, "status_detail", "$.extraction");
            RequireIntegerMap(
                extraction,
                "types_recognised",
                "$.extraction");
            OptionalIntegerMap(
                extraction,
                "types_unrecognised",
                "$.extraction");
            OptionalIntegerOrNull(extraction, "elapsed_ms", "$.extraction");
            ValidateExtractionSubrecords(extraction);

            if (data.TryGetValue("lod", out JToken lodToken) &&
                lodToken.Type != JTokenType.Null)
            {
                JObject lod = lodToken as JObject;
                if (lod == null)
                    ThrowContract("$.lod must be an object or null.");
                ValidateLodSchema(lod);
            }

            if (data.TryGetValue("summary", out JToken summaryToken))
            {
                JObject summary = summaryToken as JObject;
                if (summary == null)
                    ThrowContract("$.summary must be an object.");
                ValidateSummarySchema(summary);
            }

            if (data.TryGetValue("panels", out JToken panelsToken))
            {
                JArray panels = panelsToken as JArray;
                if (panels == null)
                    ThrowContract("$.panels must be an array.");

                for (int index = 0; index < panels.Count; index++)
                {
                    JObject panel = panels[index] as JObject;
                    if (panel == null)
                        ThrowContract($"$.panels[{index}] must be an object.");

                    ValidatePanelSchema(panel, $"$.panels[{index}]");
                }
            }

            if (data.TryGetValue("elements", out JToken elementsToken))
            {
                JArray elements = elementsToken as JArray;
                if (elements == null)
                    ThrowContract("$.elements must be an array.");

                for (int index = 0; index < elements.Count; index++)
                {
                    JObject element = elements[index] as JObject;
                    if (element == null)
                        ThrowContract($"$.elements[{index}] must be an object.");

                    ValidateElementSchema(
                        element,
                        $"$.elements[{index}]");
                }
            }

            JObject units = RequireObject(data, "units", "$");
            RequireAllowedProperties(
                units,
                "$.units",
                "area", "volume", "weight", "length");
            RequireString(units, "area", "$.units");
            RequireString(units, "volume", "$.units");
            RequireString(units, "weight", "$.units");
            RequireString(units, "length", "$.units");
            if (units.Value<string>("area") != "sqft" ||
                units.Value<string>("volume") != "m3" ||
                units.Value<string>("weight") != "kg" ||
                units.Value<string>("length") != "mm")
            {
                ThrowContract(
                    "$.units must be area=sqft, volume=m3, weight=kg, length=mm.");
            }
        }

        static void ValidateExtractionSubrecords(JObject extraction)
        {
            if (extraction.TryGetValue("blocks", out JToken blocksToken))
            {
                JObject blocks = blocksToken as JObject;
                if (blocks == null)
                    ThrowContract("$.extraction.blocks must be an object.");
                RequireAllowedProperties(
                    blocks,
                    "$.extraction.blocks",
                    "definition_count", "objects_in_blocks",
                    "overlap_with_top_level");
                OptionalInteger(
                    blocks,
                    "definition_count",
                    "$.extraction.blocks");
                OptionalIntegerMap(
                    blocks,
                    "objects_in_blocks",
                    "$.extraction.blocks");
                OptionalIntegerMap(
                    blocks,
                    "overlap_with_top_level",
                    "$.extraction.blocks");
            }

            if (extraction.TryGetValue("dropped", out JToken droppedToken) &&
                droppedToken.Type != JTokenType.Null)
            {
                JObject dropped = droppedToken as JObject;
                if (dropped == null)
                    ThrowContract("$.extraction.dropped must be an object or null.");
                RequireAllowedProperties(
                    dropped,
                    "$.extraction.dropped",
                    "where", "count", "exception");
                OptionalString(dropped, "where", "$.extraction.dropped");
                OptionalInteger(dropped, "count", "$.extraction.dropped");
                OptionalStringOrNull(
                    dropped,
                    "exception",
                    "$.extraction.dropped");
            }

            if (extraction.TryGetValue("excluded", out JToken excludedToken) &&
                excludedToken.Type != JTokenType.Null)
            {
                JObject excluded = excludedToken as JObject;
                if (excluded == null)
                    ThrowContract("$.extraction.excluded must be an object or null.");
                RequireAllowedProperties(
                    excluded,
                    "$.extraction.excluded",
                    "by_layer_policy", "reference_types");
                if (excluded.TryGetValue(
                        "by_layer_policy",
                        out JToken policiesToken))
                {
                    JArray policies = policiesToken as JArray;
                    if (policies == null)
                    {
                        ThrowContract(
                            "$.extraction.excluded.by_layer_policy must be an array.");
                    }
                    for (int index = 0; index < policies.Count; index++)
                    {
                        if (policies[index].Type != JTokenType.String)
                        {
                            ThrowContract(
                                "$.extraction.excluded.by_layer_policy " +
                                "must contain only strings.");
                        }
                    }
                }
                OptionalInteger(
                    excluded,
                    "reference_types",
                    "$.extraction.excluded");
            }
        }

        static void ValidateLodSchema(JObject lod)
        {
            const string path = "$.lod";
            RequireAllowedProperties(
                lod,
                path,
                "observed", "declared", "geometry", "sequencing", "evidence");
            foreach (string name in new[]
            {
                "observed", "declared", "geometry", "sequencing"
            })
            {
                OptionalIntegerOrNull(lod, name, path);
            }

            if (lod.TryGetValue("observed", out JToken observed) &&
                observed.Type == JTokenType.Integer)
            {
                int value = observed.Value<int>();
                if (value != 200 && value != 250 && value != 300 &&
                    value != 350 && value != 400 && value != 450)
                {
                    ThrowContract(
                        "$.lod.observed is not an allowed schema 1.0 LOD.");
                }
            }

            ValidateOptionalStringArray(lod, "evidence", path);
        }

        static void ValidatePanelSchema(
            JObject panel,
            string path)
        {
            RequireAllowedProperties(
                panel,
                path,
                "mark", "branch_id", "instance_mark", "in_block", "block_id",
                "weight_kg", "volume_m3", "gross_area_sqft", "net_area_sqft",
                "net_area_sqft_native", "rough_area_sqft", "species", "grade",
                "material_name", "material_id", "density_assigned_kg_per_m3",
                "density_subpanels_kg_per_m3", "density_sheathing_kg_per_m3",
                "weight_subpanels_kg", "weight_sheathing_kg",
                "volume_sheathing_m3", "volume_total_m3",
                "sheathing_present", "sheathing_material_name", "length_mm",
                "width_mm", "depth_mm", "lam", "logistics", "counts",
                "complexity", "subpanels", "field_errors");

            if (!panel.TryGetValue("mark", out JToken mark) ||
                (mark.Type != JTokenType.String &&
                 mark.Type != JTokenType.Null))
            {
                ThrowContract(
                    $"{path}.mark is required and must be a string or null.");
            }
            RequireString(panel, "branch_id", path);
            foreach (string name in new[]
            {
                "instance_mark", "block_id", "species", "grade",
                "material_name", "material_id", "sheathing_material_name"
            })
            {
                OptionalStringOrNull(panel, name, path);
            }
            OptionalBoolean(panel, "in_block", path);
            OptionalBooleanOrNull(
                panel,
                "sheathing_present",
                path);

            foreach (string name in new[]
            {
                "weight_kg", "volume_m3", "gross_area_sqft", "net_area_sqft",
                "net_area_sqft_native", "rough_area_sqft",
                "density_assigned_kg_per_m3", "density_subpanels_kg_per_m3",
                "density_sheathing_kg_per_m3", "weight_subpanels_kg",
                "weight_sheathing_kg", "volume_sheathing_m3", "volume_total_m3",
                "length_mm", "width_mm", "depth_mm"
            })
            {
                OptionalNumberOrNull(panel, name, path);
            }

            JObject lam = GetOptionalObjectOrNull(panel, "lam", path);
            if (lam != null)
            {
                string lamPath = $"{path}.lam";
                RequireAllowedProperties(
                    lam,
                    lamPath,
                    "profile", "arrangement", "thickness_mm", "width_mm", "height_mm",
                    "notation", "count", "stack_count");
                OptionalStringOrNull(lam, "profile", lamPath);
                OptionalStringOrNull(lam, "arrangement", lamPath);
                OptionalStringOrNull(lam, "notation", lamPath);
                OptionalNumberOrNull(lam, "thickness_mm", lamPath);
                OptionalNumberOrNull(lam, "width_mm", lamPath);
                OptionalNumberOrNull(lam, "height_mm", lamPath);
                OptionalIntegerOrNull(lam, "count", lamPath);
                OptionalIntegerOrNull(lam, "stack_count", lamPath);
            }

            JObject logistics =
                GetOptionalObjectOrNull(panel, "logistics", path);
            if (logistics != null)
            {
                string logisticsPath = $"{path}.logistics";
                RequireAllowedProperties(
                    logistics,
                    logisticsPath,
                    "fabrication_package", "truck", "bundle", "bundle_level",
                    "install_sequence");
                OptionalStringOrNull(
                    logistics,
                    "fabrication_package",
                    logisticsPath);
                OptionalStringOrNull(
                    logistics,
                    "truck",
                    logisticsPath);
                OptionalStringOrNull(
                    logistics,
                    "bundle",
                    logisticsPath);
                OptionalIntegerOrNull(
                    logistics,
                    "bundle_level",
                    logisticsPath);
                OptionalIntegerOrNull(
                    logistics,
                    "install_sequence",
                    logisticsPath);
            }

            JObject counts =
                GetOptionalObjectOrNull(panel, "counts", path);
            if (counts != null)
            {
                string countsPath = $"{path}.counts";
                string[] countNames =
                {
                    "daps_total", "daps_fastener", "daps_non_fastener",
                    "fasteners", "panel_supports", "planar_cuts"
                };
                RequireAllowedProperties(counts, countsPath, countNames);
                foreach (string name in countNames)
                    OptionalIntegerOrNull(counts, name, countsPath);
            }

            JObject complexity =
                GetOptionalObjectOrNull(panel, "complexity", path);
            if (complexity != null)
            {
                string complexityPath = $"{path}.complexity";
                RequireAllowedProperties(
                    complexity,
                    complexityPath,
                    "penetration_area_sqft", "penetration_count",
                    "corner_count", "perimeter_m", "corners_per_m",
                    "is_rectangular", "has_penetrations",
                    "blank_volume_m3", "machined_volume_m3", "machined_pct",
                    "daps_through", "daps_surface", "daps_sheathing",
                    "operation_classes", "cut_variants", "max_dap_depth_mm");
                OptionalNumberOrNull(
                    complexity,
                    "penetration_area_sqft",
                    complexityPath);
                OptionalIntegerOrNull(
                    complexity,
                    "penetration_count",
                    complexityPath);
                OptionalIntegerOrNull(
                    complexity,
                    "corner_count",
                    complexityPath);
                OptionalNumberOrNull(
                    complexity,
                    "perimeter_m",
                    complexityPath);
                OptionalNumberOrNull(
                    complexity,
                    "corners_per_m",
                    complexityPath);
                OptionalBooleanOrNull(
                    complexity,
                    "is_rectangular",
                    complexityPath);
                OptionalBooleanOrNull(
                    complexity,
                    "has_penetrations",
                    complexityPath);
                foreach (string numeric in new[]
                    {
                        "blank_volume_m3", "machined_volume_m3", "machined_pct",
                        "max_dap_depth_mm"
                    })
                {
                    OptionalNumberOrNull(complexity, numeric, complexityPath);
                }
                foreach (string counter in new[]
                    {
                        "daps_through", "daps_surface", "daps_sheathing", "cut_variants"
                    })
                {
                    OptionalIntegerOrNull(complexity, counter, complexityPath);
                }
                OptionalStringArray(complexity, "operation_classes", complexityPath);
            }

            if (panel.TryGetValue("subpanels", out JToken subpanelsToken))
            {
                JArray subpanels = subpanelsToken as JArray;
                if (subpanels == null)
                    ThrowContract($"{path}.subpanels must be an array.");

                for (int index = 0; index < subpanels.Count; index++)
                {
                    JObject subpanel = subpanels[index] as JObject;
                    if (subpanel == null)
                    {
                        ThrowContract(
                            $"{path}.subpanels[{index}] must be an object.");
                    }

                    string subpanelPath = $"{path}.subpanels[{index}]";
                    RequireAllowedProperties(
                        subpanel,
                        subpanelPath,
                        "type_letter", "net_area_sqft", "volume_m3", "width_mm", "length_mm",
                        "lam_stacks", "stack_widths_mm");
                    OptionalStringOrNull(
                        subpanel,
                        "type_letter",
                        subpanelPath);
                    OptionalNumberOrNull(
                        subpanel,
                        "net_area_sqft",
                        subpanelPath);
                    OptionalNumberOrNull(
                        subpanel,
                        "volume_m3",
                        subpanelPath);
                    OptionalNumberOrNull(
                        subpanel,
                        "width_mm",
                        subpanelPath);
                    OptionalNumberOrNull(
                        subpanel,
                        "length_mm",
                        subpanelPath);
                    ValidateOptionalIntegerArray(
                        subpanel,
                        "lam_stacks",
                        subpanelPath);
                    ValidateOptionalNumberArray(
                        subpanel,
                        "stack_widths_mm",
                        subpanelPath);
                }
            }

            ValidateOptionalStringMap(panel, "field_errors", path);
        }

        static void ValidateElementSchema(
            JObject element,
            string path)
        {
            // The element definition intentionally permits type-specific fields.
            RequireString(element, "type", path);
            RequireString(element, "id", path);
            OptionalStringOrNull(element, "host_id", path);
            OptionalBoolean(element, "in_block", path);
            OptionalStringOrNull(element, "block_id", path);
            OptionalBooleanOrNull(
                element,
                "is_fastener_dap",
                path);
            OptionalNumberOrNull(element, "weight_kg", path);
            ValidateOptionalStringMap(element, "field_errors", path);
        }

        static void ValidateSummarySchema(JObject summary)
        {
            const string path = "$.summary";
            RequireAllowedProperties(
                summary,
                path,
                "panel_count", "total_net_area_sqft", "total_volume_m3",
                "total_weight_kg", "panels_with_errors",
                "material_breakdown", "logistics");
            OptionalInteger(summary, "panel_count", path);
            OptionalInteger(summary, "panels_with_errors", path);
            OptionalNumberOrNull(summary, "total_net_area_sqft", path);
            OptionalNumberOrNull(summary, "total_volume_m3", path);
            OptionalNumberOrNull(summary, "total_weight_kg", path);

            if (summary.TryGetValue(
                    "material_breakdown",
                    out JToken breakdownToken))
            {
                JArray breakdown = breakdownToken as JArray;
                if (breakdown == null)
                {
                    ThrowContract(
                        "$.summary.material_breakdown must be an array.");
                }

                for (int index = 0; index < breakdown.Count; index++)
                {
                    JObject material = breakdown[index] as JObject;
                    if (material == null)
                    {
                        ThrowContract(
                            $"$.summary.material_breakdown[{index}] must be an object.");
                    }

                    string materialPath =
                        $"$.summary.material_breakdown[{index}]";
                    RequireAllowedProperties(
                        material,
                        materialPath,
                        "species", "layup", "panel_count", "net_area_sqft",
                        "weight_kg");
                    OptionalStringOrNull(
                        material,
                        "species",
                        materialPath);
                    OptionalStringOrNull(
                        material,
                        "layup",
                        materialPath);
                    OptionalInteger(
                        material,
                        "panel_count",
                        materialPath);
                    OptionalNumberOrNull(
                        material,
                        "net_area_sqft",
                        materialPath);
                    OptionalNumberOrNull(
                        material,
                        "weight_kg",
                        materialPath);
                }
            }

            if (summary.TryGetValue("logistics", out JToken logisticsToken))
            {
                JObject logistics = logisticsToken as JObject;
                if (logistics == null)
                    ThrowContract("$.summary.logistics must be an object.");
                const string logisticsPath = "$.summary.logistics";
                OptionalInteger(logistics, "truck_count", logisticsPath);
                OptionalInteger(logistics, "bundle_count", logisticsPath);
                OptionalInteger(
                    logistics,
                    "fabrication_package_count",
                    logisticsPath);
                OptionalIntegerOrNull(
                    logistics,
                    "install_sequence_span",
                    logisticsPath);
                OptionalNumber(
                    logistics,
                    "install_sequence_coverage_pct",
                    logisticsPath);
            }
        }

        static void RequireAllowedProperties(
            JObject value,
            string path,
            params string[] allowedNames)
        {
            var allowed = new HashSet<string>(
                allowedNames,
                StringComparer.Ordinal);
            foreach (JProperty property in value.Properties())
            {
                if (!allowed.Contains(property.Name))
                {
                    ThrowContract(
                        $"{path}.{property.Name} is not allowed by schema {SchemaVersion}.");
                }
            }
        }

        static JObject RequireObject(
            JObject value,
            string name,
            string path)
        {
            if (!(value[name] is JObject result))
                ThrowContract($"{path}.{name} is required and must be an object.");
            return (JObject)value[name];
        }

        static void RequireString(
            JObject value,
            string name,
            string path)
        {
            if (!value.TryGetValue(name, out JToken token) ||
                token.Type != JTokenType.String)
            {
                ThrowContract($"{path}.{name} is required and must be a string.");
            }
        }

        static void OptionalString(
            JObject value,
            string name,
            string path)
        {
            if (value.TryGetValue(name, out JToken token) &&
                token.Type != JTokenType.String)
            {
                ThrowContract($"{path}.{name} must be a string.");
            }
        }

        static void OptionalStringOrNull(
            JObject value,
            string name,
            string path)
        {
            if (value.TryGetValue(name, out JToken token) &&
                token.Type != JTokenType.String &&
                token.Type != JTokenType.Null)
            {
                ThrowContract($"{path}.{name} must be a string or null.");
            }
        }

        static void OptionalInteger(
            JObject value,
            string name,
            string path)
        {
            if (value.TryGetValue(name, out JToken token) &&
                token.Type != JTokenType.Integer)
            {
                ThrowContract($"{path}.{name} must be an integer.");
            }
        }

        static void OptionalIntegerOrNull(
            JObject value,
            string name,
            string path)
        {
            if (value.TryGetValue(name, out JToken token) &&
                token.Type != JTokenType.Integer &&
                token.Type != JTokenType.Null)
            {
                ThrowContract($"{path}.{name} must be an integer or null.");
            }
        }

        static void OptionalNumber(
            JObject value,
            string name,
            string path)
        {
            if (!value.TryGetValue(name, out JToken token))
                return;
            if (token.Type != JTokenType.Integer &&
                token.Type != JTokenType.Float)
            {
                ThrowContract($"{path}.{name} must be a number.");
            }
            ValidateFiniteNumber(token, $"{path}.{name}");
        }

        static void OptionalNumberOrNull(
            JObject value,
            string name,
            string path)
        {
            if (!value.TryGetValue(name, out JToken token) ||
                token.Type == JTokenType.Null)
            {
                return;
            }
            if (token.Type != JTokenType.Integer &&
                token.Type != JTokenType.Float)
            {
                ThrowContract($"{path}.{name} must be a number or null.");
            }
            ValidateFiniteNumber(token, $"{path}.{name}");
        }

        static void ValidateFiniteNumber(JToken token, string path)
        {
            double value = token.Value<double>();
            if (Double.IsNaN(value) || Double.IsInfinity(value))
                ThrowContract($"{path} must be finite.");
        }

        static void OptionalBoolean(
            JObject value,
            string name,
            string path)
        {
            if (value.TryGetValue(name, out JToken token) &&
                token.Type != JTokenType.Boolean)
            {
                ThrowContract($"{path}.{name} must be a boolean.");
            }
        }

        static void OptionalBooleanOrNull(
            JObject value,
            string name,
            string path)
        {
            if (value.TryGetValue(name, out JToken token) &&
                token.Type != JTokenType.Boolean &&
                token.Type != JTokenType.Null)
            {
                ThrowContract($"{path}.{name} must be a boolean or null.");
            }
        }

        static JObject GetOptionalObjectOrNull(
            JObject value,
            string name,
            string path)
        {
            if (!value.TryGetValue(name, out JToken token) ||
                token.Type == JTokenType.Null)
            {
                return null;
            }
            if (!(token is JObject result))
                ThrowContract($"{path}.{name} must be an object or null.");
            return (JObject)token;
        }

        static void ValidateOptionalStringArray(
            JObject value,
            string name,
            string path)
        {
            if (!value.TryGetValue(name, out JToken token))
                return;
            JArray array = token as JArray;
            if (array == null)
                ThrowContract($"{path}.{name} must be an array.");
            for (int index = 0; index < array.Count; index++)
            {
                if (array[index].Type != JTokenType.String)
                {
                    ThrowContract(
                        $"{path}.{name}[{index}] must be a string.");
                }
            }
        }

        static void OptionalStringArray(
            JObject value,
            string name,
            string path)
        {
            if (!value.TryGetValue(name, out JToken token) ||
                token.Type == JTokenType.Null)
            {
                return;
            }
            JArray array = token as JArray;
            if (array == null)
                ThrowContract($"{path}.{name} must be an array or null.");

            var allowedValues = new HashSet<string>(StringComparer.Ordinal)
            {
                "drill", "countersunk_drill", "rectangular_pocket", "shaped_pocket",
                "planar_cut", "sheathing_cut", "sharp_internal_corner"
            };

            for (int index = 0; index < array.Count; index++)
            {
                if (array[index].Type != JTokenType.String)
                {
                    ThrowContract(
                        $"{path}.{name}[{index}] must be a string.");
                }
                string value_str = array[index].Value<string>();
                if (!allowedValues.Contains(value_str))
                {
                    ThrowContract(
                        $"{path}.{name}[{index}] must be one of: drill, countersunk_drill, " +
                        "rectangular_pocket, shaped_pocket, planar_cut, sheathing_cut, sharp_internal_corner.");
                }
            }
        }

        static void ValidateOptionalIntegerArray(
            JObject value,
            string name,
            string path)
        {
            if (!value.TryGetValue(name, out JToken token) ||
                token.Type == JTokenType.Null)
            {
                return;
            }
            JArray array = token as JArray;
            if (array == null)
                ThrowContract($"{path}.{name} must be an array or null.");
            for (int index = 0; index < array.Count; index++)
            {
                if (array[index].Type != JTokenType.Integer)
                {
                    ThrowContract(
                        $"{path}.{name}[{index}] must be an integer.");
                }
            }
        }

        static void ValidateOptionalNumberArray(
            JObject value,
            string name,
            string path)
        {
            if (!value.TryGetValue(name, out JToken token) ||
                token.Type == JTokenType.Null)
            {
                return;
            }
            JArray array = token as JArray;
            if (array == null)
                ThrowContract($"{path}.{name} must be an array or null.");
            for (int index = 0; index < array.Count; index++)
            {
                if (array[index].Type != JTokenType.Integer &&
                    array[index].Type != JTokenType.Float)
                {
                    ThrowContract(
                        $"{path}.{name}[{index}] must be a number.");
                }
                ValidateFiniteNumber(array[index], $"{path}.{name}[{index}]");
            }
        }

        static void RequireIntegerMap(
            JObject value,
            string name,
            string path)
        {
            if (!(value[name] is JObject map))
                ThrowContract($"{path}.{name} is required and must be an object.");
            ValidateIntegerMap((JObject)value[name], $"{path}.{name}");
        }

        static void OptionalIntegerMap(
            JObject value,
            string name,
            string path)
        {
            if (!value.TryGetValue(name, out JToken token))
                return;
            if (!(token is JObject map))
                ThrowContract($"{path}.{name} must be an object.");
            ValidateIntegerMap((JObject)token, $"{path}.{name}");
        }

        static void ValidateIntegerMap(JObject map, string path)
        {
            foreach (JProperty property in map.Properties())
            {
                if (property.Value.Type != JTokenType.Integer)
                    ThrowContract($"{path}.{property.Name} must be an integer.");
            }
        }

        static void ValidateOptionalStringMap(
            JObject value,
            string name,
            string path)
        {
            if (!value.TryGetValue(name, out JToken token))
                return;
            if (!(token is JObject map))
                ThrowContract($"{path}.{name} must be an object.");
            foreach (JProperty property in ((JObject)token).Properties())
            {
                if (property.Value.Type != JTokenType.String)
                {
                    ThrowContract(
                        $"{path}.{name}.{property.Name} must be a string.");
                }
            }
        }

        static void ValidateOptionalObject(
            JObject value,
            string name,
            string path)
        {
            if (value.TryGetValue(name, out JToken token) &&
                token.Type != JTokenType.Object)
            {
                ThrowContract($"{path}.{name} must be an object.");
            }
        }

        static void ValidateOptionalObjectOrNull(
            JObject value,
            string name,
            string path)
        {
            if (value.TryGetValue(name, out JToken token) &&
                token.Type != JTokenType.Object &&
                token.Type != JTokenType.Null)
            {
                ThrowContract($"{path}.{name} must be an object or null.");
            }
        }

        [System.Diagnostics.CodeAnalysis.DoesNotReturn]
        static void ThrowContract(string message)
        {
            throw new ChildOutputException(
                "output_contract_invalid",
                message);
        }

        static void ValidateDroppedRecord(
            JObject data,
            DroppedRecord droppedRecord)
        {
            JObject dropped =
                data.SelectToken("extraction.dropped") as JObject;
            bool hasExceptionProperty =
                dropped?.Property("exception") != null;

            if (droppedRecord == null ||
                String.IsNullOrWhiteSpace(droppedRecord.Where) ||
                droppedRecord.Count < 0 ||
                !hasExceptionProperty)
            {
                throw new ChildOutputException(
                    "output_contract_invalid",
                    "partial_truncated requires extraction.dropped with " +
                    "{ where, count, exception }.");
            }
        }

        static JObject CreateModelResult(
            BranchExtractModel model,
            string status,
            string statusDetail,
            JObject extraction,
            int? exitCode,
            string stderr,
            string stdout,
            long elapsedMilliseconds)
        {
            string job = model.Job;
            if (String.IsNullOrWhiteSpace(job) && extraction != null)
                job = extraction.Value<string>("job");

            return new JObject
            {
                ["job"] = String.IsNullOrWhiteSpace(job)
                    ? JValue.CreateNull()
                    : new JValue(job),
                ["model_path"] = model.RequestedPath == null
                    ? JValue.CreateNull()
                    : new JValue(model.RequestedPath),
                ["status"] = status,
                ["extraction"] = extraction == null
                    ? JValue.CreateNull()
                    : extraction,
                ["elapsed_ms"] = elapsedMilliseconds,
                ["status_detail"] = statusDetail == null
                    ? JValue.CreateNull()
                    : new JValue(statusDetail),
                ["exit_code"] = exitCode.HasValue
                    ? new JValue(exitCode.Value)
                    : JValue.CreateNull(),
                ["stderr"] = String.IsNullOrEmpty(stderr)
                    ? JValue.CreateNull()
                    : new JValue(stderr)
            };
        }

        static JObject CreateRequestFailure(string detail)
        {
            return new JObject
            {
                ["any_failures"] = true,
                ["results"] = new JArray(),
                ["error"] = new JObject
                {
                    ["status"] = "invalid_request",
                    ["detail"] = detail
                }
            };
        }

        static JObject CreateBatchFailure(
            IReadOnlyList<BranchExtractModel> models,
            string code,
            string detail)
        {
            var results = new JArray();
            foreach (BranchExtractModel model in models)
            {
                results.Add(CreateModelResult(
                    model,
                    "crashed",
                    $"{code}: {detail}",
                    null,
                    null,
                    null,
                    null,
                    0));
            }

            return new JObject
            {
                ["any_failures"] = true,
                ["results"] = results
            };
        }

        static string GetChildStatus(JObject extraction)
        {
            return extraction?
                .SelectToken("extraction.status")?
                .Value<string>();
        }

        static string GetChildStatusDetail(JObject extraction)
        {
            JToken detail =
                extraction?.SelectToken("extraction.status_detail");
            return detail?.Type == JTokenType.String
                ? detail.Value<string>()
                : null;
        }

        static bool IndicatesPluginLoadFailure(string stderr)
        {
            if (String.IsNullOrWhiteSpace(stderr))
                return false;

            return ContainsIgnoreCase(stderr, "plugin_load_failed") ||
                ContainsIgnoreCase(stderr, "Branch plugin failed to load") ||
                ContainsIgnoreCase(stderr, "Branch plugin directory") ||
                (ContainsIgnoreCase(stderr, "Could not load file or assembly") &&
                    (ContainsIgnoreCase(stderr, "Branch") ||
                     ContainsIgnoreCase(stderr, "br.")));
        }

        static bool IndicatesPreBranchDocumentTimeout(string stderr)
        {
            if (String.IsNullOrWhiteSpace(stderr))
                return false;

            return ContainsIgnoreCase(
                    stderr,
                    "before BranchDoc materialized") ||
                ContainsIgnoreCase(
                    stderr,
                    "may require an interactive Branch upgrade");
        }

        static bool ContainsIgnoreCase(string value, string expected)
        {
            return value.IndexOf(
                expected,
                StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static string KillChild(Process child)
        {
            try
            {
                if (!child.HasExited)
                    child.Kill(true);

                if (!child.WaitForExit(10_000))
                {
                    return "process did not exit within 10000 ms after " +
                        "Process.Kill(entireProcessTree: true)";
                }
            }
            catch (Exception ex)
            {
                return ex.Message;
            }

            return null;
        }

        static int? TryGetExitCode(Process child)
        {
            try
            {
                return child.HasExited
                    ? child.ExitCode
                    : (int?)null;
            }
            catch
            {
                return null;
            }
        }

        static string ReadCapturedStream(
            Task<string> streamTask,
            string streamName)
        {
            try
            {
                if (!streamTask.Wait(5_000))
                {
                    return $"{streamName} capture did not complete within 5000 ms";
                }

                return streamTask.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                return $"{streamName} capture failed: {ex.Message}";
            }
        }

        static string HashModelPath(string modelPath)
        {
            byte[] hash = SHA256.HashData(
                Encoding.UTF8.GetBytes(modelPath));
            return Convert.ToHexString(hash)
                .Substring(0, 16)
                .ToLowerInvariant();
        }

        static string ResolveOverridePath(string configuredPath)
        {
            string trimmed = configuredPath.Trim().Trim('"');
            return Path.IsPathFullyQualified(trimmed)
                ? Path.GetFullPath(trimmed)
                : Path.GetFullPath(
                    Path.Combine(AppContext.BaseDirectory, trimmed));
        }

        static int ReadConfiguredTimeoutMilliseconds()
        {
            string configured =
                Environment.GetEnvironmentVariable(
                    "BRANCH_EXTRACT_TIMEOUT_MS");
            if (String.IsNullOrWhiteSpace(configured))
                return DefaultTimeoutMilliseconds;

            if (!Int32.TryParse(
                    configured,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int timeoutMilliseconds) ||
                timeoutMilliseconds <= 0)
            {
                Log.Warning(
                    "Ignoring invalid BRANCH_EXTRACT_TIMEOUT_MS={Configured}; using {Default}",
                    configured,
                    DefaultTimeoutMilliseconds);
                return DefaultTimeoutMilliseconds;
            }

            return timeoutMilliseconds;
        }

        static long ReadConfiguredMaximumOutputBytes()
        {
            string configured =
                Environment.GetEnvironmentVariable(
                    "BRANCH_EXTRACT_MAX_OUTPUT_BYTES");
            if (String.IsNullOrWhiteSpace(configured))
                return DefaultMaximumOutputBytes;

            if (!Int64.TryParse(
                    configured,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out long maximumOutputBytes) ||
                maximumOutputBytes <= 0)
            {
                Log.Warning(
                    "Ignoring invalid BRANCH_EXTRACT_MAX_OUTPUT_BYTES={Configured}; using {Default}",
                    configured,
                    DefaultMaximumOutputBytes);
                return DefaultMaximumOutputBytes;
            }

            return maximumOutputBytes;
        }

        static void SweepTempDirectory(string tempDirectory)
        {
            int removed = 0;
            foreach (string path in Directory.EnumerateFiles(
                tempDirectory,
                "*",
                SearchOption.TopDirectoryOnly))
            {
                File.Delete(path);
                removed++;
            }

            if (removed > 0)
            {
                Log.Information(
                    "Removed {Count} stale branch extractor temp files from {TempDirectory}",
                    removed,
                    tempDirectory);
            }
        }

        static string DeleteModelTempFiles(string outputPath)
        {
            var failures = new List<string>();
            try
            {
                if (File.Exists(outputPath))
                    File.Delete(outputPath);
            }
            catch (Exception ex)
            {
                failures.Add($"{outputPath}: {ex.Message}");
                Log.Error(
                    ex,
                    "Unable to delete branch extractor output {OutputPath}",
                    outputPath);
            }

            string directory =
                Path.GetDirectoryName(outputPath);
            string fileName =
                Path.GetFileName(outputPath);
            try
            {
                foreach (string sidecar in Directory.EnumerateFiles(
                    directory,
                    fileName + ".*.tmp",
                    SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        File.Delete(sidecar);
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{sidecar}: {ex.Message}");
                        Log.Error(
                            ex,
                            "Unable to delete branch extractor temp sidecar {OutputPath}",
                            sidecar);
                    }
                }
            }
            catch (Exception ex)
            {
                failures.Add(
                    $"unable to enumerate temp sidecars for {outputPath}: {ex.Message}");
                Log.Error(
                    ex,
                    "Unable to enumerate branch extractor temp sidecars for {OutputPath}",
                    outputPath);
            }

            return failures.Count == 0
                ? null
                : String.Join(" | ", failures);
        }

        static void AppendStatusDetail(
            JObject result,
            string detail)
        {
            string existing =
                result.Value<string>("status_detail");
            result["status_detail"] = CombineDetails(existing, detail);
        }

        static string CombineDetails(params string[] details)
        {
            var populated = new List<string>();
            foreach (string detail in details)
            {
                if (!String.IsNullOrWhiteSpace(detail))
                    populated.Add(detail);
            }

            return populated.Count == 0
                ? null
                : String.Join(" ", populated);
        }

        static void EnsureInitialized()
        {
            if (!s_initialized)
                Initialize();
        }

        sealed class BranchExtractRequest
        {
            internal IReadOnlyList<BranchExtractModel> Models { get; set; }
            internal int? TimeoutMilliseconds { get; set; }
        }

        sealed class BranchExtractModel
        {
            internal string Job { get; set; }
            internal string RequestedPath { get; set; }
            internal string ResolvedPath { get; set; }
            internal string ValidationFailure { get; set; }
        }

        // Only the supervisor-relevant portion of the frozen child schema is
        // duplicated here. The original JObject is retained and returned
        // wholesale so fields owned by branch.extract are never discarded.
        sealed class ExtractionRecord
        {
            [JsonProperty("schema_version")]
            public string SchemaVersion { get; set; }

            [JsonProperty("extraction")]
            public ExtractionMetadata Extraction { get; set; }
        }

        sealed class ExtractionMetadata
        {
            [JsonProperty("status")]
            public string Status { get; set; }

            [JsonProperty("status_detail")]
            public string StatusDetail { get; set; }

            [JsonProperty("dropped")]
            public DroppedRecord Dropped { get; set; }
        }

        sealed class DroppedRecord
        {
            [JsonProperty("where")]
            public string Where { get; set; }

            [JsonProperty("count")]
            public int Count { get; set; }

            [JsonProperty("exception")]
            public string Exception { get; set; }
        }

        sealed class RequestValidationException : Exception
        {
            internal RequestValidationException(string message)
                : base(message)
            {
            }
        }

        sealed class ChildOutputException : Exception
        {
            internal ChildOutputException(
                string code,
                string message)
                : base(message)
            {
                Code = code;
            }

            internal ChildOutputException(
                string code,
                string message,
                Exception innerException)
                : base(message, innerException)
            {
                Code = code;
            }

            internal string Code { get; }
        }
    }
}
