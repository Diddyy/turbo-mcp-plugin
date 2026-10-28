using System.Threading;
using System.Threading.Tasks;
using Turbo.Contracts.Plugins.Web;

namespace TurboMcp;

public sealed class McpResourceMetadataRoute(McpOAuth oauth) : IPluginWebRoute
{
    public string Method => "GET";
    public string Pattern => ".well-known/oauth-protected-resource/mcp";
    public bool Anonymous => true;
    public bool AnyOrigin => true;

    public Task<PluginWebResult> HandleAsync(PluginWebRequest request, CancellationToken ct) =>
        Task.FromResult(oauth.Metadata(true));
}

public sealed class McpAuthorizationMetadataRoute(McpOAuth oauth) : IPluginWebRoute
{
    public string Method => "GET";
    public string Pattern => ".well-known/oauth-authorization-server";
    public bool Anonymous => true;
    public bool AnyOrigin => true;

    public Task<PluginWebResult> HandleAsync(PluginWebRequest request, CancellationToken ct) =>
        Task.FromResult(oauth.Metadata(false));
}

public sealed class McpRegistrationRoute(McpOAuth oauth) : IPluginWebRoute
{
    public string Method => "POST";
    public string Pattern => "oauth/register";
    public bool Anonymous => true;
    public bool AnyOrigin => true;
    public bool CredentialHeaders => true;

    public Task<PluginWebResult> HandleAsync(PluginWebRequest request, CancellationToken ct) =>
        Task.FromResult(oauth.Register(request));
}

public sealed class McpAuthorizationRoute(McpOAuth oauth) : IPluginWebRoute
{
    public string Method => "GET";
    public string Pattern => "oauth/authorize";
    public bool Anonymous => true;

    public Task<PluginWebResult> HandleAsync(PluginWebRequest request, CancellationToken ct) =>
        oauth.AuthorizeAsync(request, ct);
}

public sealed class McpConsentRoute(McpOAuth oauth) : IPluginWebRoute
{
    public string Method => "POST";
    public string Pattern => "oauth/consent";

    public Task<PluginWebResult> HandleAsync(PluginWebRequest request, CancellationToken ct) =>
        oauth.ConsentAsync(request, ct);
}

public sealed class McpTokenRoute(McpOAuth oauth) : IPluginWebRoute
{
    public string Method => "POST";
    public string Pattern => "oauth/token";
    public bool Anonymous => true;
    public bool AnyOrigin => true;
    public bool CredentialHeaders => true;

    public Task<PluginWebResult> HandleAsync(PluginWebRequest request, CancellationToken ct) =>
        oauth.TokenAsync(request, ct);
}
