using Couplet.Core.Security;

namespace Couplet.Tests;

public sealed class SecurityContractTests
{
    [Fact]
    public void Validate_LocalOnlyPolicy_ReturnsNoProblems()
    {
        SecurityPolicy policy = CreatePolicy(new ProviderPolicy
        {
            Mode = ProviderMode.LocalOnly,
            AllowedFields = [],
            UserOptIn = false,
        });

        Assert.Empty(SecurityPolicyValidator.Validate(policy));
    }

    [Fact]
    public void Validate_OnlineProviderWithoutExplicitOptIn_ReturnsStableProblems()
    {
        SecurityPolicy policy = CreatePolicy(new ProviderPolicy
        {
            Mode = ProviderMode.ExplicitOnline,
            ProviderId = "provider",
            ModelId = "model",
            ModelVersion = "v1",
            AllowedFields = ["content_hash"],
            UserOptIn = false,
        });

        Assert.Equal(["online_provider_requires_opt_in"], SecurityPolicyValidator.Validate(policy));
    }

    private static SecurityPolicy CreatePolicy(ProviderPolicy provider) => new()
    {
        WorkspaceAllowlist = ["workspace"],
        IgnorePatterns = ["**/bin/**", "**/obj/**"],
        DenyPatterns = ["**/.env", "**/*secret*"],
        Provider = provider,
        Lifecycle = new DataLifecyclePolicy
        {
            RetiredGenerationRetention = TimeSpan.FromDays(1),
            LogRetention = TimeSpan.FromDays(7),
            ProviderCacheRetention = TimeSpan.Zero,
            DeleteIndexOnWorkspaceRemoval = true,
        },
        LogRelativePaths = false,
    };
}
