$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$project = Join-Path $PSScriptRoot '../src/Monstertacle.Hosting.Tool/Monstertacle.Hosting.Tool.csproj'
$version = (dotnet msbuild $project -getProperty:PackageVersion).Trim()
$scratch = Join-Path ([IO.Path]::GetTempPath()) "hosting-tool-$([Guid]::NewGuid())"
$feed = Join-Path $scratch 'feed'
$manifest = Join-Path $scratch '.config'
$originalPackages = $env:NUGET_PACKAGES

New-Item -ItemType Directory -Path $feed, $manifest | Out-Null
try {
    dotnet pack $project -c Release --no-restore -o $feed
    @{
        version = 1
        isRoot = $true
        tools = @{
            'monstertacle.hosting.tool' = @{
                version = $version
                commands = @('monstertacle-hosting')
            }
        }
    } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $manifest 'dotnet-tools.json')
    $feedXml = [Security.SecurityElement]::Escape($feed)
    "<configuration><packageSources><clear/><add key=`"local`" value=`"$feedXml`" /></packageSources></configuration>" |
        Set-Content (Join-Path $scratch 'NuGet.Config')

    Push-Location $scratch
    try {
        $env:NUGET_PACKAGES = Join-Path $scratch 'packages'
        dotnet tool restore --configfile NuGet.Config
        $helpText = dotnet tool run monstertacle-hosting -- --help
        if ($helpText -notlike '*monstertacle-hosting submit*status*lookup*') {
            throw 'The restored tool did not provide the expected help.'
        }
    } finally {
        Pop-Location
    }
} finally {
    $env:NUGET_PACKAGES = $originalPackages
    Remove-Item -LiteralPath $scratch -Recurse -Force
}
