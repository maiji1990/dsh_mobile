using System.Text;
using System.Text.Json;
using DshMobile.Agent.Domain;
using DshMobile.Agent.Engines;

namespace DshMobile.Agent.Sdui;

/// <summary>
/// 插件 SDUI 文档生成服务（Slice 1B-2）：通过一个专用隐藏会话让 Agent 只读分析已安装插件包，
/// 收集其输出的单个 JSON 文档，经安全校验后写入版本化缓存。
/// 任何创建、发送、等待、解析、校验或落盘失败都返回稳定诊断键，绝不伪造空面板。
/// </summary>
public sealed class PluginSduiGeneratorService
{
    /// <summary>诊断键：网关未连接或未声明 Prompt 能力。</summary>
    public const string GeneratorUnavailableDiagnostic = "sdui.generator_unavailable";

    /// <summary>诊断键：包名非法。</summary>
    public const string InvalidPackageDiagnostic = "sdui.invalid_package";

    /// <summary>诊断键：创建专用辅助会话失败。</summary>
    public const string SessionCreateFailedDiagnostic = "sdui.session_create_failed";

    /// <summary>诊断键：发送分析提示词失败。</summary>
    public const string PromptSendFailedDiagnostic = "sdui.prompt_send_failed";

    /// <summary>诊断键：会话等待失败。</summary>
    public const string WaitFailedDiagnostic = "sdui.wait_failed";

    /// <summary>诊断键：Agent 要求用户输入，已取消本轮。</summary>
    public const string InputRequiredDiagnostic = "sdui.input_required";

    /// <summary>诊断键：生成总超时。</summary>
    public const string GenerationTimeoutDiagnostic = "sdui.generation_timeout";

    /// <summary>诊断键：读取会话事件失败。</summary>
    public const string EventsLoadFailedDiagnostic = "sdui.events_load_failed";

    /// <summary>诊断键：Agent 输出为空。</summary>
    public const string EmptyOutputDiagnostic = "sdui.empty_output";

    /// <summary>诊断键：输出不是合法 JSON。</summary>
    public const string JsonParseFailedDiagnostic = "sdui.json_parse_failed";

    /// <summary>诊断键：文档未通过 SDUI 安全校验。</summary>
    public const string SchemaInvalidDiagnostic = "sdui.schema_invalid";

    /// <summary>诊断键：文档落盘失败。</summary>
    public const string SaveFailedDiagnostic = "sdui.save_failed";

    /// <summary>诊断键：Agent 主动以 <c>{"error":"..."}</c> 报告无法完成分析。</summary>
    public const string AgentReportedErrorDiagnostic = "sdui.agent_reported_error";

    /// <summary>诊断键：结构与 Schema 校验通过后，只读试跑 RPC 数据源仍被协议/输入错误拒绝。</summary>
    public const string ProbeFailedDiagnostic = "sdui.probe_failed";

    /// <summary>结构/JSON 纠偏与只读试跑纠偏共享的总轮次上限。</summary>
    private const int MaxRepairRounds = 2;

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan MaximumWaitChunk = TimeSpan.FromSeconds(15);

    private readonly IDshGateway _gateway;
    private readonly PluginSduiStore _store;
    private readonly HiddenSessionStore? _hiddenSessionStore;
    private readonly TimeSpan _timeout;

    /// <summary>创建生成服务。</summary>
    /// <param name="gateway">DSH 网关。</param>
    /// <param name="store">SDUI 版本化缓存存储。</param>
    /// <param name="hiddenSessionStore">可选的本机隐藏会话存储；提供时辅助会话不会出现在用户会话列表。</param>
    /// <param name="timeout">生成总超时；为空时使用 5 分钟。</param>
    public PluginSduiGeneratorService(
        IDshGateway gateway,
        PluginSduiStore store,
        HiddenSessionStore? hiddenSessionStore = null,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(store);
        _gateway = gateway;
        _store = store;
        _hiddenSessionStore = hiddenSessionStore;
        _timeout = timeout is { } requested && requested > TimeSpan.Zero ? requested : DefaultTimeout;
    }

    /// <summary>
    /// 生成并保存指定插件的 SDUI 文档。辅助会话会立即加入本机隐藏集合，并在结束时尽力归档。
    /// </summary>
    /// <param name="packageName">npm 包名。</param>
    /// <param name="expectedVersion">目标版本；为空时由 Agent 从 package.json 读取。</param>
    /// <param name="settingsNamespaceHint">可选的插件配置命名空间提示。</param>
    /// <param name="ct">取消令牌（外部取消异常不会被吞掉）。</param>
    public async Task<Outcome<PluginSduiDocument>> GenerateAsync(
        string packageName,
        string? expectedVersion = null,
        string? settingsNamespaceHint = null,
        CancellationToken ct = default)
    {
        var result = await GenerateDetailedAsync(packageName, expectedVersion, settingsNamespaceHint, ct)
            .ConfigureAwait(false);
        if (result is { Found: true, Document: not null })
        {
            return Outcome<PluginSduiDocument>.Ok(result.Document);
        }

        var diagnosticKey = result.DiagnosticKey ?? SaveFailedDiagnostic;
        return Outcome<PluginSduiDocument>.Fail(FailureCodeFor(diagnosticKey), diagnosticKey);
    }

    /// <summary>
    /// 生成并保存指定插件的 SDUI 文档，并保留可展示的校验错误。解析、Schema 或只读试跑失败时，
    /// 在同一个辅助会话上追加纠偏提示；结构纠偏与试跑纠偏合计最多 <see cref="MaxRepairRounds"/> 轮，
    /// 仍失败则返回 <see cref="PluginSduiLoadResult.Found"/> = false，绝不落盘。
    /// </summary>
    /// <param name="packageName">npm 包名。</param>
    /// <param name="expectedVersion">目标版本；为空时由 Agent 从 package.json 读取。</param>
    /// <param name="settingsNamespaceHint">可选的插件配置命名空间提示。</param>
    /// <param name="ct">取消令牌（外部取消异常不会被吞掉）。</param>
    public async Task<PluginSduiLoadResult> GenerateDetailedAsync(
        string packageName,
        string? expectedVersion = null,
        string? settingsNamespaceHint = null,
        CancellationToken ct = default)
    {
        if (!PluginPackageValidator.IsValidPackageName(packageName))
        {
            return new PluginSduiLoadResult(false, DiagnosticKey: InvalidPackageDiagnostic);
        }

        if (!_gateway.Capabilities.Prompt)
        {
            return new PluginSduiLoadResult(false, DiagnosticKey: GeneratorUnavailableDiagnostic);
        }

        string? sessionId = null;
        try
        {
            var create = await _gateway
                .CreateOrResumeSessionAsync(new CreateOrResumeSessionRequest(), ct)
                .ConfigureAwait(false);
            if (!create.IsOk || string.IsNullOrWhiteSpace(create.Value))
            {
                return new PluginSduiLoadResult(
                    false,
                    DiagnosticKey: create.Error?.DiagnosticKey ?? SessionCreateFailedDiagnostic);
            }

            sessionId = create.Value!;

            // 立即隐藏辅助会话，避免生成过程污染用户的会话列表。
            if (_hiddenSessionStore is not null)
            {
                try
                {
                    await _hiddenSessionStore.HideAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _ = ex;
                }
            }

            var prompt = BuildPrompt(packageName, expectedVersion, settingsNamespaceHint);
            var initialRound = await RequestAttemptAsync(sessionId, prompt, ct).ConfigureAwait(false);
            if (!initialRound.IsOk)
            {
                return new PluginSduiLoadResult(
                    false,
                    DiagnosticKey: initialRound.Error?.DiagnosticKey ?? PromptSendFailedDiagnostic);
            }

            var attempt = initialRound.Value!;
            var repairRounds = 0;
            while (true)
            {
                if (attempt.CanCorrect)
                {
                    if (repairRounds >= MaxRepairRounds)
                    {
                        // 结构/JSON 纠偏已用完全部轮次，保留最后一次可展示诊断。
                        return new PluginSduiLoadResult(
                            false,
                            DiagnosticKey: attempt.DiagnosticKey ?? SchemaInvalidDiagnostic,
                            ValidationErrors: attempt.Errors);
                    }

                    var correction = BuildCorrectionPrompt(attempt);
                    var correctedRound = await RequestAttemptAsync(sessionId, correction, ct).ConfigureAwait(false);
                    if (!correctedRound.IsOk)
                    {
                        return new PluginSduiLoadResult(
                            false,
                            DiagnosticKey: correctedRound.Error?.DiagnosticKey ?? PromptSendFailedDiagnostic,
                            ValidationErrors: attempt.Errors);
                    }

                    attempt = correctedRound.Value!;
                    repairRounds++;
                    continue;
                }

                if (!attempt.IsValid || attempt.Document is null)
                {
                    return new PluginSduiLoadResult(
                        false,
                        DiagnosticKey: attempt.DiagnosticKey ?? SchemaInvalidDiagnostic,
                        ValidationErrors: attempt.Errors);
                }

                // 结构校验通过后只读试跑 rpc 数据源；actions 一律不触发。
                var probeErrors = await ProbeRpcSourcesAsync(attempt.Document, ct).ConfigureAwait(false);
                if (probeErrors.Count == 0)
                {
                    break;
                }

                if (repairRounds >= MaxRepairRounds)
                {
                    // 纠偏轮次耗尽仍被协议/输入层拒绝：失败关闭，绝不落盘或伪造成功。
                    return new PluginSduiLoadResult(
                        false,
                        DiagnosticKey: ProbeFailedDiagnostic,
                        ValidationErrors: probeErrors);
                }

                var probeCorrection = BuildProbeCorrectionPrompt(probeErrors);
                var probedRound = await RequestAttemptAsync(sessionId, probeCorrection, ct).ConfigureAwait(false);
                if (!probedRound.IsOk)
                {
                    return new PluginSduiLoadResult(
                        false,
                        DiagnosticKey: probedRound.Error?.DiagnosticKey ?? PromptSendFailedDiagnostic,
                        ValidationErrors: probeErrors);
                }

                attempt = probedRound.Value!;
                repairRounds++;
            }

            var saved = await _store
                .SaveGeneratedAsync(attempt.Document, packageName, expectedVersion, ct)
                .ConfigureAwait(false);
            if (!saved.Found || saved.Document is null)
            {
                return new PluginSduiLoadResult(
                    false,
                    DiagnosticKey: saved.DiagnosticKey ?? SaveFailedDiagnostic,
                    ValidationErrors: saved.ValidationErrors);
            }

            return saved;
        }
        finally
        {
            if (sessionId is not null)
            {
                try
                {
                    await _gateway
                        .ArchiveSessionAsync(sessionId, stopActivity: true, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 归档属于尽力清理，失败不覆盖主流程结果；外部取消异常仍会继续向上抛出。
                    _ = ex;
                }
            }
        }
    }

    /// <summary>发送一轮提示词并读取、解析助手输出；校验失败以成功的 <see cref="SduiGenerationAttempt"/> 返回。</summary>
    private async Task<Outcome<SduiGenerationAttempt>> RequestAttemptAsync(
        string sessionId,
        string prompt,
        CancellationToken ct)
    {
        var send = await _gateway
            .SendPromptAsync(
                new PromptSendRequest(Guid.NewGuid().ToString("N"), sessionId, prompt),
                ct)
            .ConfigureAwait(false);
        if (!send.IsOk)
        {
            return Outcome<SduiGenerationAttempt>.Fail(
                send.Error ?? new DshFailure(DshFailureCode.Unavailable, PromptSendFailedDiagnostic));
        }

        var read = await ReadAssistantTextAsync(sessionId, ct).ConfigureAwait(false);
        if (!read.IsOk)
        {
            return Outcome<SduiGenerationAttempt>.Fail(
                read.Error ?? new DshFailure(DshFailureCode.Unavailable, EventsLoadFailedDiagnostic));
        }

        return Outcome<SduiGenerationAttempt>.Ok(ParseAndValidate(read.Value ?? string.Empty));
    }

    /// <summary>
    /// 在结构与 Schema 校验通过后只读试跑 rpc 数据源：仅使用空的 <c>sources</c> 根与文档初始 UI 状态，
    /// 绝不触发 <c>actions</c>。仅协议/输入层错误进入纠偏列表，业务错误、网关不可用与超时保持静默。
    /// </summary>
    private async Task<IReadOnlyList<string>> ProbeRpcSourcesAsync(
        PluginSduiDocument document,
        CancellationToken ct)
    {
        if (document.Sources is not { Count: > 0 } sources)
        {
            return [];
        }

        var emptySources = CreateEmptyObjectElement();
        var context = new SduiEvalContext
        {
            Sources = emptySources,
            UiState = document.InitialUiState,
        };

        var errors = new List<string>();
        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested();
            if (!string.Equals(source.Kind, SduiDataSourceKinds.Rpc, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(source.Endpoint))
            {
                continue;
            }

            var args = PluginSduiEvaluator.MaterializeTemplate(source.ArgsTemplate, context) ?? emptySources;
            Outcome<JsonElement> outcome;
            try
            {
                outcome = await _gateway
                    .InvokePluginRpcAsync(source.Endpoint!, args, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 外部取消必须原样透传，不能被试跑逻辑吞掉。
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 传输异常不代表插件输入契约有误，不进入纠偏。
                _ = ex;
                continue;
            }

            if (outcome.IsOk || !RequiresProbeCorrection(outcome.Error))
            {
                continue;
            }

            var endpoint = SecretRedactor.RedactAndTruncate(source.Endpoint!, 160);
            var diagnostic = SecretRedactor.RedactAndTruncate(
                outcome.Error?.DiagnosticKey ?? outcome.Error?.Code.ToString() ?? "unknown",
                160);
            var argsSummary = SecretRedactor.RedactAndTruncate(args.GetRawText(), 400);
            errors.Add($"数据源 {source.Id}（端点 {endpoint}）只读试跑失败：{diagnostic}；参数摘要：{argsSummary}");
        }

        return errors;
    }

    /// <summary>仅把协议/输入层失败视为需要纠偏；业务错误与网关不可用等不影响落盘。</summary>
    private static bool RequiresProbeCorrection(DshFailure? failure)
    {
        if (failure is null)
        {
            return false;
        }

        if (failure.Code == DshFailureCode.ProtocolMismatch)
        {
            return true;
        }

        var diagnostic = failure.DiagnosticKey;
        if (string.IsNullOrWhiteSpace(diagnostic))
        {
            return false;
        }

        return diagnostic switch
        {
            "gateway/input-invalid" or
            "gateway/invalid-input" or
            "gateway/bad-request" or
            "gateway/validation-failed" or
            "gateway/method-not-found" or
            "typert/input-invalid" or
            "plugin_sdui.forbidden_rpc_endpoint" => true,
            _ when diagnostic.EndsWith("/input-invalid", StringComparison.Ordinal) => true,
            _ => false,
        };
    }

    /// <summary>创建只读空对象根；不复用可变 DOM，避免生命周期越界。</summary>
    private static JsonElement CreateEmptyObjectElement()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    private async Task<Outcome<string>> ReadAssistantTextAsync(string sessionId, CancellationToken ct)
    {
        var wait = await WaitUntilIdleAsync(sessionId, ct).ConfigureAwait(false);
        if (!wait.IsOk)
        {
            return Outcome<string>.Fail(wait.Error!);
        }

        var page = await _gateway
            .LoadSessionEventsAsync(sessionId, maxMessages: 50, ct: ct)
            .ConfigureAwait(false);
        if (!page.IsOk)
        {
            return Outcome<string>.Fail(
                page.Error ?? new DshFailure(DshFailureCode.Unavailable, EventsLoadFailedDiagnostic));
        }

        return Outcome<string>.Ok(ExtractLastAssistantText(page.Value!.Events));
    }

    private static SduiGenerationAttempt ParseAndValidate(string assistantText)
    {
        var json = StripJsonFence(assistantText);
        if (string.IsNullOrWhiteSpace(json))
        {
            return SduiGenerationAttempt.Failed(EmptyOutputDiagnostic, []);
        }

        PluginSduiDocument? document;
        try
        {
            // 先探测 Agent 的显式报错：{"error":"..."} 不是待纠偏的 Schema 问题，
            // 而是 Agent 已明确无法完成任务；立即失败，不再追加 Self-Repair 轮次。
            using (var probe = JsonDocument.Parse(json))
            {
                if (TryReadAgentReportedError(probe.RootElement, out var summary))
                {
                    return SduiGenerationAttempt.Failed(AgentReportedErrorDiagnostic, [summary]);
                }
            }

            document = JsonSerializer.Deserialize(json, PluginSduiJsonContext.Default.PluginSduiDocument);
        }
        catch (JsonException ex)
        {
            var line = ex.LineNumber is { } lineNumber ? lineNumber + 1 : (long?)null;
            var column = ex.BytePositionInLine is { } bytePosition ? bytePosition + 1 : (long?)null;
            var location = line is null
                ? "未知位置"
                : $"第 {line} 行第 {column ?? 1} 列";
            var message = ex.Message.Length > 400 ? ex.Message[..400] : ex.Message;
            return SduiGenerationAttempt.Failed(
                JsonParseFailedDiagnostic,
                [$"JSON 解析失败（{location}）：{message}"]);
        }

        if (document is null)
        {
            return SduiGenerationAttempt.Failed(JsonParseFailedDiagnostic, ["JSON 解析结果为空。"]);
        }

        var validation = PluginSduiValidator.Validate(document);
        if (!validation.IsValid)
        {
            return SduiGenerationAttempt.Failed(SchemaInvalidDiagnostic, validation.Errors);
        }

        return SduiGenerationAttempt.Succeeded(document);
    }

    /// <summary>
    /// 识别 Agent 显式报错对象：根为 Object、含字符串 <c>error</c>、且不含 <c>schemaVersion</c>。
    /// 命中时输出脱敏、限长的摘要，供 UI 展示。
    /// </summary>
    private static bool TryReadAgentReportedError(JsonElement root, out string summary)
    {
        summary = string.Empty;
        if (root.ValueKind != JsonValueKind.Object ||
            root.TryGetProperty("schemaVersion", out _) ||
            !root.TryGetProperty("error", out var error) ||
            error.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        summary = SummarizeAgentError(error.GetString());
        return true;
    }

    /// <summary>折叠控制字符与空白并限长，避免把整段日志或路径明细透传到 UI。</summary>
    private static string SummarizeAgentError(string? message)
    {
        const int maxLength = 200;
        var builder = new StringBuilder(maxLength + 32);
        foreach (var ch in message ?? string.Empty)
        {
            if (builder.Length >= maxLength)
            {
                break;
            }

            if (char.IsControl(ch))
            {
                if (ch is '\r' or '\n' or '\t')
                {
                    builder.Append(' ');
                }

                continue;
            }

            builder.Append(ch);
        }

        var text = string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (text.Length == 0)
        {
            text = "Agent 未提供错误详情。";
        }

        return $"Agent 报告错误：{text}";
    }

    private static string BuildCorrectionPrompt(SduiGenerationAttempt attempt)
    {
        var builder = new StringBuilder(1024);
        builder.AppendLine("你上一次输出的 SDUI JSON 未通过校验，不能保存。");
        if (string.Equals(attempt.DiagnosticKey, JsonParseFailedDiagnostic, StringComparison.Ordinal))
        {
            builder.AppendLine("解析失败摘要：");
            builder.Append("- ").AppendLine(
                attempt.Errors.Count > 0 ? attempt.Errors[0] : "JSON 无法解析。");
        }
        else
        {
            builder.AppendLine("校验错误（最多展示前 8 条）：");
            foreach (var error in attempt.Errors.Take(8))
            {
                builder.Append("- ").AppendLine(error);
            }
        }

        builder.AppendLine("请直接重新输出一个完整的 JSON 对象，不要解释，不要 Markdown 代码围栏。");
        builder.AppendLine("必须保持 schemaVersion=1、包名与实际版本正确，并遵守：metric_grid/key_value_list 的 items 非空；list_section 必须有 itemsBind；segmented_switch 必须有 stateKey 且 options 至少 2 项；action_bar 必须有 actions；settings 数据源必须有 namespace；refreshIntervalSec 若填写必须 >= 15；所有 *Bind 至少两段路径（如 sources.usage.rolling.percent）。");
        builder.AppendLine("若涉及 argsTemplate 或 payloadTemplate，必须重新查阅 lib/types/**/*.d.ts（优先 contracts.d.ts）中的输入类型、枚举、正则与必填字段；所有可能取不到初始值的 $bind 必须用 {\"$bind\":\"...\",\"$default\":...} 提供符合类型约束的默认值。");
        return builder.ToString();
    }

    private static string BuildProbeCorrectionPrompt(IReadOnlyList<string> probeErrors)
    {
        var builder = new StringBuilder(1024);
        builder.AppendLine("你上一次输出的 SDUI JSON 已通过结构与 Schema 校验，但只读试跑 RPC 数据源时被网关以协议/输入错误拒绝，不能保存。");
        builder.AppendLine("失败明细（端点、错误码与参数摘要均已脱敏，最多展示前 8 条）：");
        foreach (var error in probeErrors.Take(8))
        {
            builder.Append("- ").AppendLine(error);
        }

        builder.AppendLine("请先只读核对 lib/types/**/*.d.ts（优先 lib/types/contracts.d.ts）中每个相关 RPC 的输入类型、字段必填性、枚举取值、正则与数值范围，再修正 sources 的 argsTemplate。");
        builder.AppendLine("所有可能取不到初始值的 $bind（尤其 sources.*.argsTemplate 中引用 ui.* / sources.* 的字段）必须提供 $default，且 $default 必须符合 .d.ts 约束，不得用 null 或任意字符串敷衍。");
        builder.AppendLine("只允许修正只读数据源参数；不得新增或调用任何 actions，不得执行写操作，也不得修改无关结构。");
        builder.AppendLine("请直接重新输出一个完整的 JSON 对象，不要解释，不要 Markdown 代码围栏。");
        return builder.ToString();
    }

    private static DshFailureCode FailureCodeFor(string diagnosticKey) => diagnosticKey switch
    {
        InvalidPackageDiagnostic or EmptyOutputDiagnostic or SchemaInvalidDiagnostic or InputRequiredDiagnostic =>
            DshFailureCode.InvalidState,
        JsonParseFailedDiagnostic or ProbeFailedDiagnostic => DshFailureCode.ProtocolMismatch,
        GenerationTimeoutDiagnostic => DshFailureCode.Timeout,
        SaveFailedDiagnostic or PluginSduiStore.DocumentTooLargeDiagnostic or PluginSduiStore.CacheWriteFailedDiagnostic =>
            DshFailureCode.StorageUnavailable,
        _ => DshFailureCode.Unavailable,
    };

    private sealed record SduiGenerationAttempt(
        PluginSduiDocument? Document,
        string? DiagnosticKey,
        IReadOnlyList<string> Errors)
    {
        public bool IsValid => Document is not null && DiagnosticKey is null;

        public bool CanCorrect => DiagnosticKey is JsonParseFailedDiagnostic or SchemaInvalidDiagnostic;

        public static SduiGenerationAttempt Succeeded(PluginSduiDocument document) =>
            new(document, null, []);

        public static SduiGenerationAttempt Failed(string diagnosticKey, IReadOnlyList<string> errors) =>
            new(null, diagnosticKey, errors);
    }

    /// <summary>
    /// 在总超时内等待会话进入 Idle；Timeout 允许续等，InputRequired 会取消本轮并失败。
    /// </summary>
    private async Task<Outcome<Unit>> WaitUntilIdleAsync(string sessionId, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + _timeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return Outcome<Unit>.Fail(DshFailureCode.Timeout, GenerationTimeoutDiagnostic);
            }

            var chunk = remaining < MaximumWaitChunk ? remaining : MaximumWaitChunk;
            var wait = await _gateway
                .WaitForSessionAsync(sessionId, chunk, ct: ct)
                .ConfigureAwait(false);
            if (!wait.IsOk)
            {
                return Outcome<Unit>.Fail(
                    wait.Error ?? new DshFailure(DshFailureCode.Unavailable, WaitFailedDiagnostic));
            }

            switch (wait.Value!.Kind)
            {
                case SessionWaitOutcomeKind.Idle:
                    return Outcome<Unit>.Ok(Unit.Value);

                case SessionWaitOutcomeKind.Timeout:
                    // 分片超时只代表本轮等待窗口耗尽，继续在总超时内等待。
                    continue;

                case SessionWaitOutcomeKind.InputRequired:
                    await TryCancelTurnAsync(sessionId).ConfigureAwait(false);
                    return Outcome<Unit>.Fail(DshFailureCode.InvalidState, InputRequiredDiagnostic);

                default:
                    return Outcome<Unit>.Fail(DshFailureCode.Unavailable, WaitFailedDiagnostic);
            }
        }
    }

    /// <summary>取消当前轮次；失败不覆盖原始诊断。</summary>
    private async Task TryCancelTurnAsync(string sessionId)
    {
        try
        {
            await _gateway.CancelTurnAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = ex;
        }
    }

    /// <summary>从后往前查找最后一条 assistant/message，并拼接其中的 text 片段。</summary>
    private static string ExtractLastAssistantText(IReadOnlyList<SessionWireEventView>? events)
    {
        if (events is null)
        {
            return string.Empty;
        }

        for (var index = events.Count - 1; index >= 0; index--)
        {
            var wireEvent = events[index];
            if (wireEvent is null || !string.Equals(wireEvent.Type, "assistant/message", StringComparison.Ordinal))
            {
                continue;
            }

            var text = ExtractMessageText(wireEvent.Data);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return string.Empty;
    }

    /// <summary>提取 <c>data.message.content[]</c> 中 <c>type = text</c> 的片段文本。</summary>
    private static string ExtractMessageText(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        var container = data.TryGetProperty("message", out var nested) ? nested : data;
        if (container.ValueKind != JsonValueKind.Object ||
            !container.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object ||
                !part.TryGetProperty("type", out var type) ||
                type.ValueKind != JsonValueKind.String ||
                !string.Equals(type.GetString(), "text", StringComparison.Ordinal) ||
                !part.TryGetProperty("text", out var text) ||
                text.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append('\n');
            }
            builder.Append(text.GetString());
        }

        return builder.ToString();
    }

    /// <summary>剥离 Markdown 的 <c>```json</c> / <c>```</c> 围栏；无围栏时返回原文。</summary>
    private static string StripJsonFence(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        var opening = trimmed.IndexOf("```", StringComparison.Ordinal);
        if (opening < 0)
        {
            return trimmed;
        }

        var afterOpening = opening + 3;
        if (trimmed.AsSpan(afterOpening).StartsWith("json", StringComparison.OrdinalIgnoreCase))
        {
            afterOpening += 4;
        }

        var contentStart = trimmed.IndexOf('\n', afterOpening);
        if (contentStart < 0)
        {
            return trimmed;
        }
        contentStart++;

        var closing = trimmed.IndexOf("```", contentStart, StringComparison.Ordinal);
        var content = closing < 0 ? trimmed[contentStart..] : trimmed[contentStart..closing];
        return content.Trim();
    }

    /// <summary>
    /// 构造精确只读分析提示词：限定可读文件、禁止修改、输出单个严格 JSON，
    /// 并明确 v1 Schema、9 种区块、动作引用、绑定白名单与 RPC 参数包裹结构。
    /// </summary>
    private static string BuildPrompt(
        string packageName,
        string? expectedVersion,
        string? settingsNamespaceHint)
    {
        var versionHint = string.IsNullOrWhiteSpace(expectedVersion)
            ? "以 package.json 中的实际版本为准"
            : expectedVersion.Trim();
        var namespaceHint = string.IsNullOrWhiteSpace(settingsNamespaceHint)
            ? "如需 settings 数据源，请从 package.json / README 推断官方配置命名空间"
            : $"官方配置命名空间为 {settingsNamespaceHint.Trim()}";

        var builder = new StringBuilder(2048);
        builder.AppendLine("你是只读的 DSH 插件界面分析器。");
        builder.Append("目标 npm 包：").Append(packageName).AppendLine("。");
        builder.Append("目标版本：").Append(versionHint).AppendLine("。");
        builder.AppendLine("插件安装目录为当前工作区下的 .dsh/profiles/web/node_modules/" + packageName + "（完整路径为 /root/projects/.dsh/profiles/web/node_modules/" + packageName + "，即 $DSH_HOME/profiles/web/node_modules/" + packageName + "）。");
        builder.AppendLine("优先只读查看以下内容以最快速度提取全部 typert 端点、参数包裹与 settings 字段：");
        builder.AppendLine("- package.json（版本、入口、配置命名空间）");
        builder.AppendLine("- lib/types/contracts.d.ts，或 lib/types/**/*.d.ts 中的类型声明");
        builder.AppendLine("- README.md（用法、端点与配置说明）");
        builder.AppendLine("有 .d.ts 时无需通读打包压缩的 lib/index.js；仅在缺少类型声明时，再按需检索 lib/index.js 中对应的端点与参数结构。");
        builder.AppendLine("若已存在 lib/types/**/*.d.ts（含 lib/types/contracts.d.ts），严禁再去检索或读取打包 JS（lib/index.js、lib/chunk-*.js），也不要编写并执行 Python 自检脚本；直接按本提示词规范输出最终 JSON 对象。");
        builder.AppendLine("在编写任何 argsTemplate 或 payloadTemplate 之前，必须先逐个 RPC 查阅 lib/types/**/*.d.ts（优先 lib/types/contracts.d.ts）中的输入类型声明，核对字段名、必填/可选、枚举取值、正则与数值范围；不得凭猜测编造参数包裹。");
        builder.AppendLine("不得写入、修改、创建或删除任何文件，不得执行安装命令或访问网络。若目录不存在或无法读取，只输出 {\"error\":\"...\"} 的 JSON。");
        builder.AppendLine("只读范围仅限上面列出的目标插件目录（$DSH_HOME/profiles/web/node_modules/" + packageName + "，完整路径 /root/projects/.dsh/profiles/web/node_modules/" + packageName + "）内的文件；严禁读取或检索 .dsh/sessions、.dsh/dshmobile、/opt、/usr，也严禁对 / 执行递归搜索（如 grep -r、find /）去寻找 dshmobile SDUI schema 或 validator。");
        builder.AppendLine("本地运行镜像中不存在任何额外的 SDUI schema 或文档：本提示词已是完整且唯一的 dshmobile SDUI v1 规范。只读上述目标插件目录内的文件后，立即直接输出单个 JSON 对象，不要再搜索或探测任何其它路径。");
        builder.AppendLine();
        builder.AppendLine("请严格输出一个 JSON 对象，不要输出解释文字。JSON 必须符合 dshmobile SDUI v1：");
        builder.AppendLine("- schemaVersion 必须为 1；pluginPackage 必须为 \"" + packageName + "\"；pluginVersion 为实际版本；title 非空。");
        builder.AppendLine("- tabs 为 1..8 个，每个含 id、label、blocks；整份文档 blocks 总数不超过 64。");
        builder.AppendLine("- sources 不超过 16 个，kind 只能是 rpc 或 settings；rpc.endpoint 必须是两段式 namespace/method，且不得使用保留命名空间：session、commands、credentials、settings、pluginInventory、pluginManager、workspace、fs、system、relay、ui、remote、connection、webserver、typert。");
        builder.AppendLine("  sources 每项的精确字段（驼峰，不得改名）：id、kind(\"rpc\"|\"settings\")、endpoint、namespace、argsTemplate、refreshIntervalSec、autoLoad。");
        builder.AppendLine("  kind=rpc 时必须给 endpoint（namespace/method 两段式）与可选 argsTemplate；kind=settings 时必须给 namespace 且不要给 endpoint。refreshIntervalSec 若出现必须 >= 15。");
        builder.AppendLine("  settings 数据源返回 { namespace, value, ... }，绑定其中配置项必须写 sources.<id>.value.<field>（不要写 sources.<id>.<field>）。");
        builder.AppendLine("- blocks.type 只能是 9 种之一：status_banner、metric_grid、quota_bar、key_value_list、segmented_switch、list_section、action_bar、external_link、settings_bridge。");
        builder.AppendLine("  每种 blocks 的精确字段（驼峰，未列出的字段一律不要输出）：");
        builder.AppendLine("  status_banner: tone(\"info\"|\"warn\"|\"success\"|\"danger\")、title、titleBind、subtitle、subtitleBind。");
        builder.AppendLine("  quota_bar: label、percentBind、statusBind、resetAtBind。");
        builder.AppendLine("  metric_grid: items:[{ label, valueBind, unit, format, monospace }]。");
        builder.AppendLine("  key_value_list: items:[{ label, valueBind, unit, format, monospace }]。");
        builder.AppendLine("  segmented_switch: label、stateKey、options:[{ value, label }]、onChangeActionId。");
        builder.AppendLine("  list_section: title、itemsBind、emptyText、itemTitleBind、itemSubtitleBind、itemBadgeBind、itemProgressBind、itemActions:[{ label, style, actionId, confirm, confirmTitle, confirmMessage }]。");
        builder.AppendLine("  action_bar: actions:[{ label, style(\"primary\"|\"danger\"|\"text\")、actionId、confirm、confirmTitle、confirmMessage }]。");
        builder.AppendLine("  external_link: label、url（绝对 https://）。");
        builder.AppendLine("  settings_bridge: namespace。");
        builder.AppendLine("  list_section 的 itemTitleBind/itemSubtitleBind/itemBadgeBind/itemProgressBind 必须写成 item.<字段>（相对当前条目），不要写 sources.*。");
        builder.AppendLine("- 必须遵守的校验约束：metric_grid/key_value_list 的 items 非空；list_section 必须有 itemsBind；segmented_switch 必须有 stateKey 且 options >= 2；action_bar 必须有 actions；settings 数据源必须有 namespace；refreshIntervalSec 若填写须 >= 15；所有 *Bind 至少两段路径（如 sources.usage.rolling.percent）。");
        builder.AppendLine("- actions 不超过 32 个，kind 只能是 rpc、settings_mutate、open_form、open_url、refresh_sources、set_ui_state。");
        builder.AppendLine("  actions 每项的精确字段（驼峰，未列出的字段一律不要输出）：id、kind、endpoint、payloadTemplate、targets、title、submitAction、onSuccessRefresh、toastResultBind、url、stateKey、stateValue，以及 open_form 的 fields。");
        builder.AppendLine("  fields:[{ key, label, type(\"text\"|\"password\"|\"number\"|\"select\"|\"bool\")、required、maxLength、options:[{ value, label }]、defaultValue、placeholder }]。");
        builder.AppendLine("  open_form 的 submitAction 必须是 actions 中某个 action 的 id 字符串（不是对象、不是内联动作），绝不能是内联对象；提交动作使用 rpc 或 settings_mutate，可用 $bind form.<field> 引用用户输入。");
        builder.AppendLine("  当插件存在需要用户输入参数的变更型 RPC（例如新增账号、填写 API Key/Token 等凭据操作）时，必须为其生成 open_form 动作，并在对应页签的 action_bar 中提供按钮；其中密码、API Key、Token 等敏感输入字段的 type 必须设为 \"password\"，不得用 \"text\"。");
        builder.AppendLine("  settings_mutate 的 endpoint 填插件官方配置命名空间（不要带 /method），payloadTemplate 顶层属性会成为 settings/mutate 的 Set 路径。");
        builder.AppendLine("  rpc 动作/数据源的 payloadTemplate 与 argsTemplate 必须按插件的实际类型包裹，例如 { \"command\": { ... } }、{ \"query\": { ... } }、{ \"input\": { ... } }，不要臆造扁平参数。");
        builder.AppendLine("  若需了解界面分页与交互结构，可额外只读参考 lib/types/client/*.d.ts（仍然只读，不得修改）。");
        builder.Append("- $bind 只允许白名单根 sources、ui、item、form、result，形如 sources.usage.items.0.limit；不得包含括号、脚本或其它根。").AppendLine();
        builder.AppendLine("  $bind 的默认值语法必须是 {\"$bind\":\"sources.usage.items.0.limit\",\"$default\":<默认值>}；$default 只在绑定解析不到值时使用。");
        builder.AppendLine("  所有在初始状态下可能取不到值的 $bind（尤其 sources 的 argsTemplate 中引用 ui.* / sources.* 的字段）都必须提供 $default，且 $default 必须符合 .d.ts 的类型、枚举、正则与必填约束；禁止用 null、空字符串或任意猜测值敷衍。");
        builder.AppendLine("- $gen 只允许 hex32 或 uuid。");
        builder.AppendLine("- external_link 与 open_url 的 url 只允许绝对 https:// 地址。");
        builder.AppendLine("- format 只允许 plain、number、microCentsUsd、relativeTime。");
        builder.Append("- ").Append(namespaceHint).AppendLine("。");
        builder.AppendLine("- 若插件声明了 settingsNamespace（或 package.json / README 表明存在可配置项），必须在设置相关页签中挂载至少一个 settings_bridge 区块，namespace 与文档声明一致；不得只声明 settings 数据源而不提供设置入口。");
        builder.AppendLine("- RPC 端点与参数包裹结构必须根据 lib/types/**/*.d.ts 中的实际类型确认，不得臆造；typert 风格调用常见包裹为 { \"input\": {} }、{ \"command\": ... }、{ \"query\": ... }。");
        builder.AppendLine();
        builder.AppendLine("只输出单个 JSON 对象，不要输出 Markdown 代码围栏之外的任何内容。");
        return builder.ToString();
    }
}
