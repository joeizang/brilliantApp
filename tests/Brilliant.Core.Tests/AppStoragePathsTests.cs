using Brilliant.Core.Apps;

namespace Brilliant.Core.Tests;

public class AppStoragePathsTests
{
    private static string Dsa => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "dsa-manifest.yaml"));

    [Fact]
    public void Dsa_keeps_the_installed_apps_directory_and_secure_key()
    {
        var storage = new AppStoragePaths(AppManifest.Parse(Dsa), "/existing/data");

        Assert.Equal("/existing/data", storage.DataDirectory);
        Assert.Equal("brilliant.device-credentials", storage.CredentialsKey);
    }

    [Fact]
    public void Two_other_apps_have_separate_data_and_credentials_even_on_a_shared_platform_root()
    {
        var a = new AppStoragePaths(AppManifest.Parse(Dsa.Replace(AppStoragePaths.DsaAppId, "example.one")), "/shared/data");
        var b = new AppStoragePaths(AppManifest.Parse(Dsa.Replace(AppStoragePaths.DsaAppId, "example.two")), "/shared/data");

        Assert.Equal(Path.Combine("/shared/data", "apps", "example.one"), a.DataDirectory);
        Assert.NotEqual(a.DataDirectory, b.DataDirectory);
        Assert.NotEqual(a.CredentialsKey, b.CredentialsKey);
        Assert.NotEqual("brilliant.device-credentials", a.CredentialsKey);
    }
}
