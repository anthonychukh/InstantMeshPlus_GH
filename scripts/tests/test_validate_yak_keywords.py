"""Regression coverage for the public Yak keyword rejection."""

import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import zipfile


REPO = Path(__file__).resolve().parents[2]
GUID = "a8053c38-fdfa-4d99-bc31-b285e4bb59c5"


class KeywordValidationTests(unittest.TestCase):
    def validate(self, keywords):
        with tempfile.TemporaryDirectory() as directory:
            archive = Path(directory) / "instantmeshplus-0.1.3-rh8_32-win.yak"
            manifest = "name: instantmeshplus\nversion: 0.1.3\nkeywords:\n" + keywords
            with zipfile.ZipFile(archive, "w") as package:
                package.writestr("manifest.yml", manifest)
            result = subprocess.run(
                [sys.executable, str(REPO / "scripts/validate_yak_archives.py"),
                 "--root", directory, "--package-name", "instantmeshplus",
                 "--expected-version", "0.1.3"],
                capture_output=True, text=True, check=False,
            )
            return result.returncode, json.loads(result.stdout)

    def test_rejects_spaces_and_punctuation_before_upload(self):
        for keyword in ["instant mesh", "rhino 8", "quad.mesh", "'curve guides'", "guid:invalid"]:
            with self.subTest(keyword=keyword):
                code, report = self.validate(f"- {keyword}\n")
                self.assertEqual(code, 1)
                self.assertIn("Invalid manifest keyword", " ".join(report["errors"]))

    def test_accepts_keywords_and_yak_generated_guid(self):
        code, report = self.validate(f"- instant-mesh\n- rhino_8\n- 'quad'\n- guid:{GUID}\n")
        self.assertEqual(code, 0, report)

    def test_repository_keyword_list_is_publishable(self):
        keywords = (REPO / "yak/manifest.yml").read_text().split("keywords:\n", 1)[1]
        code, report = self.validate(keywords + f"  - guid:{GUID}\n")
        self.assertEqual(code, 0, report)


if __name__ == "__main__":
    unittest.main()
