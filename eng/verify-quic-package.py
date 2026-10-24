#!/usr/bin/env python3
"""Exercise NuGet buildTransitive assets using a fresh, external consumer.

Usage: python3 eng/verify-quic-package.py artifacts/LightDl.<version>.nupkg
Requires .NET 10 SDK and NuGet access. Windows managed publishing works on Unix.
"""
import pathlib
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
import zipfile

package = pathlib.Path(sys.argv[1]).resolve()
with zipfile.ZipFile(package) as z:
    metadata = ET.fromstring(z.read("LightDl.nuspec"))
    version = metadata.find("{*}metadata/{*}version").text
    names = set(z.namelist())
    assert "buildTransitive/LightDl.targets" in names
    assert "tools/PatchQuic.dll" in names
    assert len([n for n in names if n.endswith("/libmsquic.a")]) == 8
    assert len([n for n in names if n.startswith("buildTransitive/native/net11/") and n.endswith(".a")]) == 4
    assert not any(n.endswith("/msquic.dll") for n in names)

with tempfile.TemporaryDirectory(prefix="lightdl-package-check-") as tmp:
    root = pathlib.Path(tmp)
    project = root / "Check.csproj"
    project.write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup>
  <ItemGroup><PackageReference Include="LightDl" Version="{version}" /></ItemGroup>
</Project>''')
    (root / "Program.cs").write_text('System.Console.WriteLine(typeof(LightDl.LightDownloader).FullName);')

    def publish(mode, *properties, succeeds=True):
        result = subprocess.run([
            "dotnet", "publish", str(project), "-c", "Release", "-r", "win-x64",
            "--self-contained", "true", "--source", str(package.parent),
            "--source", "https://api.nuget.org/v3/index.json", "-o", str(root / mode),
            *properties,
        ], capture_output=True, text=True)
        if (result.returncode == 0) != succeeds:
            raise AssertionError(result.stdout + result.stderr)
        return result.stdout + result.stderr

    publish("default")
    assert not list((root / "default").glob("*msquic*"))
    assert not list((root / "bin").rglob("msquic.dll")), "Default build copied msquic.dll"
    publish("shared", "-p:LightDlIncludeMsQuic=true")
    assert (root / "shared/msquic.dll").is_file()
    assert "Choose either" in publish("conflict", "-p:LightDlIncludeMsQuic=true",
        "-p:LightDlStaticQuic=true", "-p:PublishAot=true", succeeds=False)
    assert "PublishAot=true" in publish("no-aot", "-p:LightDlStaticQuic=true", succeeds=False)
    print(f"PASS: {version}: package assets, default omission, shared opt-in, and invalid option diagnostics")
