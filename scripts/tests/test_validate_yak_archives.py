from __future__ import annotations

import importlib.util
import struct
import unittest
from pathlib import Path


MODULE_PATH = Path(__file__).parents[1] / "validate_yak_archives.py"
SPEC = importlib.util.spec_from_file_location("validate_yak_archives", MODULE_PATH)
assert SPEC is not None and SPEC.loader is not None
VALIDATOR = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(VALIDATOR)


class ValidatorHelpersTests(unittest.TestCase):
    def test_reads_universal_mach_o_architectures(self) -> None:
        header = struct.pack(
            ">IIiiIIIiiIII",
            0xCAFEBABE,
            2,
            0x01000007,
            3,
            48,
            1,
            0,
            0x0100000C,
            0,
            49,
            1,
            0,
        )
        self.assertEqual(
            {"arm64", "x86_64"}, VALIDATOR.macho_architectures(header + b"xx")
        )

    def test_reads_64_bit_universal_mach_o_architectures(self) -> None:
        header = struct.pack(
            ">IIiiQQIIiiQQII",
            0xCAFEBABF,
            2,
            0x01000007,
            3,
            72,
            1,
            0,
            0,
            0x0100000C,
            0,
            73,
            1,
            0,
            0,
        )
        self.assertEqual(
            {"arm64", "x86_64"}, VALIDATOR.macho_architectures(header + b"xx")
        )

    def test_parses_platform_specific_entries(self) -> None:
        self.assertEqual(
            {"mac": {"instantmeshplus.worker", "libtbb.12.dylib"}},
            VALIDATOR.platform_entries(
                ["mac:InstantMeshPlus.Worker", "mac:libtbb.12.dylib"], "--test"
            ),
        )

    def test_rejects_malformed_platform_entry(self) -> None:
        with self.assertRaises(ValueError):
            VALIDATOR.platform_entries(["InstantMeshPlus.Worker"], "--test")


if __name__ == "__main__":
    unittest.main()
