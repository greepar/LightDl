#!/usr/bin/env python3
"""Avoid shell/platform conversions of CLI --source lists on Windows."""
import pathlib
import xml.etree.ElementTree as ET
root = ET.Element("configuration")
sources = ET.SubElement(root, "packageSources")
ET.SubElement(sources, "clear")
ET.SubElement(sources, "add", key="local", value=str(pathlib.Path("artifacts").resolve()))
ET.SubElement(sources, "add", key="nuget.org", value="https://api.nuget.org/v3/index.json")
ET.ElementTree(root).write("Quic.NuGet.Config", encoding="utf-8", xml_declaration=True)
