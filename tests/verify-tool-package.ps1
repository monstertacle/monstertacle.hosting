param(
    [string] $ExpectedVersion,
    [string] $PackageOutput,
    [switch] $ValidateIdentityOnly
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$packageId = 'Monstertacle.Hosting.Tool'
$project = Join-Path $PSScriptRoot '../src/Monstertacle.Hosting.Tool/Monstertacle.Hosting.Tool.csproj'
$properties = (dotnet msbuild $project -property:Configuration=Release '-getProperty:PackageId,PackageVersion,PackAsTool' | Out-String | ConvertFrom-Json).Properties
$version = $properties.PackageVersion
if ($properties.PackageId -cne $packageId -or $properties.PackAsTool -ne 'true') {
    throw "The project must pack the $packageId .NET tool."
}
if ($PSBoundParameters.ContainsKey('ExpectedVersion') -and $ExpectedVersion -cne $version) {
    throw "The expected version must match the project's package version ($version)."
}
if ($ValidateIdentityOnly) {
    return
}

$packageName = "$packageId.$version.nupkg"
$scratch = Join-Path ([IO.Path]::GetTempPath()) "hosting-tool-$([Guid]::NewGuid())"
$feed = Join-Path $scratch 'feed'
$manifest = Join-Path $scratch '.config'
$originalPackages = $env:NUGET_PACKAGES

New-Item -ItemType Directory -Path $feed, $manifest | Out-Null
try {
    dotnet pack $project -c Release --no-restore -o $feed
    $packages = @(Get-ChildItem -LiteralPath $feed -Force)
    if ($packages.Count -ne 1 -or $packages[0].PSIsContainer -or $packages[0].Name -cne $packageName) {
        throw "Pack must produce exactly $packageName."
    }
    $package = $packages[0].FullName
    $archive = [IO.Compression.ZipFile]::OpenRead($package)
    try {
        $entry = $archive.GetEntry("$packageId.nuspec")
        if ($null -eq $entry) {
            throw 'The package is missing the expected nuspec.'
        }
        $reader = [IO.StreamReader]::new($entry.Open())
        try {
            [xml] $nuspec = $reader.ReadToEnd()
        } finally {
            $reader.Dispose()
        }
        $metadata = $nuspec.package.metadata
        if ($metadata.id -cne $packageId -or $metadata.version -cne $version -or
            $metadata.packageTypes.packageType.name -cne 'DotnetTool') {
            throw 'The nuspec does not match the expected tool identity.'
        }
    } finally {
        $archive.Dispose()
    }

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
    Copy-Item (Join-Path $PSScriptRoot '../global.json') $scratch

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

    if ($PackageOutput) {
        New-Item -ItemType Directory -Path $PackageOutput -Force | Out-Null
        if (Get-ChildItem -LiteralPath $PackageOutput -Force) {
            throw 'The artifact staging directory must be empty.'
        }
        Move-Item -LiteralPath $package -Destination $PackageOutput
    }
} finally {
    $env:NUGET_PACKAGES = $originalPackages
    Remove-Item -LiteralPath $scratch -Recurse -Force
}
