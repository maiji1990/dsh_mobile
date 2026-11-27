using DshMobile.Agent.Domain;
using DshMobile.Agent.Protocol;
using DshMobile.Agent.Transport;

namespace DshMobile.Agent.Tests.Domain;

public sealed class OutcomeAndFailureTests
{
    private sealed class EmptyStubGateway : DefaultDshGateway;

    [Fact]
    public void Outcome_SupportsOkFailMapBindAndMatch()
    {
        var ok = Outcome<int>.Ok(21)
            .Map(x => x * 2)
            .Bind(x => Outcome<string>.Ok($"v={x}"));

        Assert.True(ok.IsOk);
        Assert.Equal("v=42", ok.Value);
        Assert.Null(ok.Error);
        Assert.Equal("v=42", ok.Match(v => v, err => err.Code.ToString()));

        var failed = Outcome<int>.Fail(DshFailureCode.CredentialsMissing, "cred.missing", retryable: false)
            .Map(x => x + 1);

        Assert.False(failed.IsOk);
        Assert.Equal(DshFailureCode.CredentialsMissing, failed.Error!.Code);
        Assert.Equal("cred.missing", failed.Match(_ => "ok", err => err.DiagnosticKey!));
    }

    [Fact]
    public void FailureMapper_MapsKnownRemoteErrorCodesAndExceptions_WithoutLeakingRawMessages()
    {
        Assert.Equal(
            DshFailureCode.CredentialsMissing,
            DshFailureMapper.FromRpcError(new RemoteRpcError("session/provider-credentials-unavailable", "sensitive details")).Code);
        Assert.Equal(
            DshFailureCode.SessionBusy,
            DshFailureMapper.FromRpcError(new RemoteRpcError("session/model-unavailable", "model down")).Code);
        Assert.Equal(
            DshFailureCode.InvalidState,
            DshFailureMapper.FromRpcError(new RemoteRpcError("session/invalid-time-zone", "bad tz")).Code);
        Assert.Equal(
            DshFailureCode.CorruptHistory,
            DshFailureMapper.FromRpcError(new RemoteRpcError("session/migration-required", "old schema")).Code);
        Assert.Equal(
            DshFailureCode.Timeout,
            DshFailureMapper.FromRpcError(new RemoteRpcError("session/wait-timeout", "timed out")).Code);
        Assert.Equal(
            DshFailureCode.SessionActive,
            DshFailureMapper.FromRpcError(new RemoteRpcError("workspace/session-active", "session running")).Code);
        Assert.Equal(
            DshFailureCode.SessionNotFound,
            DshFailureMapper.FromRpcError(new RemoteRpcError("session/not-found", "missing")).Code);
        Assert.Equal(
            DshFailureCode.Unauthorized,
            DshFailureMapper.FromException(new RemoteAuthenticationException("secret token xyz", 401)).Code);
        Assert.Equal(
            DshFailureCode.ProtocolMismatch,
            DshFailureMapper.FromException(new RemoteProtocolException("raw frame")).Code);
        Assert.Equal(
            DshFailureCode.TransportLost,
            DshFailureMapper.FromException(new RemoteDrainingException()).Code);
    }

    [Fact]
    public async Task DefaultDshGateway_ReturnsUnavailableAcrossAllFacets()
    {
        var gw = new EmptyStubGateway();
        Assert.Equal(DshCapabilities.Empty, gw.Capabilities);
        Assert.Equal(DshFailureCode.Unavailable, (await gw.ProbeCapabilitiesAsync()).Error!.Code);
        Assert.Equal(DshFailureCode.Unavailable, (await gw.ListSessionsAsync()).Error!.Code);
        Assert.Equal(DshFailureCode.Unavailable, (await gw.SendPromptAsync(new PromptSendRequest("r1", "s1", "hi"))).Error!.Code);
        Assert.Equal(DshFailureCode.Unavailable, (await gw.ReplyApprovalAsync("c1", "e1", ApprovalDecision.Rejected)).Error!.Code);
        Assert.Equal(DshFailureCode.Unavailable, (await gw.GetModelCatalogAsync()).Error!.Code);
        Assert.Equal(DshFailureCode.Unavailable, (await gw.GetPluginInventoryAsync()).Error!.Code);
        Assert.Equal(DshFailureCode.Unavailable, (await gw.ArchiveSessionAsync("s1")).Error!.Code);
        Assert.Equal(DshFailureCode.Unavailable, (await gw.UnarchiveSessionAsync("s1")).Error!.Code);
        Assert.Equal(DshFailureCode.Unavailable, (await gw.GetArchivedSessionIdsAsync()).Error!.Code);
        Assert.Equal(DshFailureCode.Unavailable, (await gw.FollowWorkspaceAsync(_ => ValueTask.CompletedTask)).Error!.Code);
    }

    [Fact]
    public void IanaTimeZoneResolver_NormalizesAndValidatesTimeZones()
    {
        var shanghai = IanaTimeZoneResolver.Resolve("Asia/Shanghai");
        Assert.Equal("Asia/Shanghai", shanghai);

        var utc = IanaTimeZoneResolver.Resolve("Etc/UTC");
        Assert.Equal("UTC", utc);

        var windowsMapped = IanaTimeZoneResolver.Resolve("China Standard Time");
        Assert.Equal("Asia/Shanghai", windowsMapped);

        Assert.Throws<ArgumentException>(() => IanaTimeZoneResolver.Resolve("Mars/Olympus_Mons"));
    }

    [Fact]
    public void ImageAttachmentValidator_EnforcesMimeAndByteLimits()
    {
        var tinyPng = Convert.ToBase64String([0x89, 0x50, 0x4E, 0x47]);
        Assert.Null(ImageAttachmentValidator.Validate([new PromptAttachmentImage("image/png", tinyPng, "a.png")]));

        var badMime = ImageAttachmentValidator.Validate([new PromptAttachmentImage("image/svg+xml", tinyPng)]);
        Assert.Equal("attachment.unsupported_media_type", badMime!.DiagnosticKey);

        var badBase64 = ImageAttachmentValidator.Validate([new PromptAttachmentImage("image/png", "%%%not-base64%%%")]);
        Assert.Equal("attachment.invalid_base64", badBase64!.DiagnosticKey);

        var strictLimits = new ImageUploadLimitsSpec(
            MaxImageBytes: 2,
            MaxImagesPerMessage: 1,
            MaxMessageImageBytes: 2,
            AllowedMediaTypes: ImageUploadLimitsSpec.DefaultMediaTypes);
        var tooLarge = ImageAttachmentValidator.Validate([new PromptAttachmentImage("image/png", tinyPng)], strictLimits);
        Assert.Equal("attachment.image_too_large", tooLarge!.DiagnosticKey);
    }
}
