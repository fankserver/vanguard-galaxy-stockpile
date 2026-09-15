import json
import pathlib
import stat
import tempfile
import unittest
import zipfile
from package import METADATA, NAMES, check_compiled_version, check_metadata, resource_version, validate

ROOT = pathlib.Path(__file__).resolve().parents[1]
SIDECAR = (ROOT / METADATA).read_bytes()


class PackageTests(unittest.TestCase):
    def test_layout_and_rejections(self):
        for change in ("valid", "missing", "empty", "symlink", "extra", "traversal"):
            with self.subTest(change=change), tempfile.TemporaryDirectory() as root:
                path = pathlib.Path(root) / "test.zip"
                with zipfile.ZipFile(path, "w") as archive:
                    for name in NAMES:
                        if change == "missing" and name == "LICENSE":
                            continue
                        info = zipfile.ZipInfo("VGStockpile/" + name)
                        if change == "symlink" and name == "LICENSE":
                            info.create_system = 3
                            info.external_attr = (stat.S_IFLNK | 0o777) << 16
                        payload = SIDECAR if name == METADATA else b"synthetic"
                        archive.writestr(info, b"" if change == "empty" else payload)
                    if change in ("extra", "traversal"):
                        archive.writestr("VGStockpile/Assembly-CSharp.dll" if change == "extra" else "../outside", b"synthetic")
                if change == "valid":
                    validate(path)
                else:
                    with self.assertRaises(ValueError):
                        validate(path)

    def test_shipped_sidecar_is_valid(self):
        metadata = check_metadata(SIDECAR)
        self.assertEqual("vgstockpile", metadata["pluginId"])
        self.assertTrue(metadata["description"].strip())

    def test_sidecar_rejections(self):
        base = json.loads(SIDECAR)
        for change in ({"pluginId": "vgmodapi"}, {"channel": "experimental"}, {"schemaVersion": 2},
                       {"version": "1.2.3"}, {"description": ""}, {"unknown": "x"},
                       {"updateUrl": "https://example.org/update.json"}):
            with self.subTest(change=change):
                with self.assertRaises(ValueError):
                    check_metadata(json.dumps({**base, **change}).encode("utf-8"))
        with self.assertRaises(ValueError):
            check_metadata(b'{"schemaVersion":1,"pluginId":"vgstockpile","pluginId":"vgstockpile"}')

    def test_compiled_version_gate(self):
        built = ROOT / "VGStockpile/bin/Release/netstandard2.1/VGStockpile.dll"
        if not built.is_file():
            self.skipTest("Build the plugin before running packaging tests")
        data = built.read_bytes()
        version = resource_version(data, "Assembly Version").rsplit(".", 1)[0]
        check_compiled_version(data, version)
        major, minor, patch = version.split(".")
        for other in (f"{major}.{minor}.{int(patch) + 1}", f"{major}.{int(minor) + 1}.{patch}"):
            with self.subTest(version=other), self.assertRaises(ValueError):
                check_compiled_version(data, other)

    def test_reference_versions_cannot_satisfy_the_gate(self):
        """A referenced assembly's version string must never stand in for our own.

        Reference and type strings carrying versions are incidental to what the build
        happens to reference, so this uses a synthetic payload rather than asserting a
        particular reference survives in the shipped assembly."""
        for payload, version in ((b"Assembly-CSharp, Version=0.0.0.0, Culture=neutral", "0.0.0"),
                                 (b"Some.Other, Version=1.2.3.0, PublicKeyToken=null", "1.2.3")):
            with self.subTest(version=version), self.assertRaises(ValueError):
                check_compiled_version(payload, version)
        with self.assertRaises(ValueError):
            check_compiled_version(b"no version resource here", "0.8.1")


if __name__ == "__main__":
    unittest.main()
