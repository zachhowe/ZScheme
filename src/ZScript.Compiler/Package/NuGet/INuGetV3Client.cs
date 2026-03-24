namespace ZScript.Compiler.Package.NuGet;

internal interface INuGetV3Client : IDisposable
{
    Task<string> GetPackageBaseAddressAsync();
    Task<IReadOnlyList<string>> GetVersionsAsync(string packageId);
    Task DownloadNupkgAsync(string packageId, string version, string destinationPath);
}
