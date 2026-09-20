"""Keep Android manifest metadata and the native loader contract consistent with build receipts."""
import json
from pathlib import Path
import tempfile
import unittest
import xml.etree.ElementTree as ET

from build_nativeaot import validate_android_manifest


class AndroidManifestTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.path = Path(self.temp.name) / 'AndroidManifest.xml'
        self.manifest = json.loads(Path(__file__).with_name('nativeaot.manifest.json').read_text())
        self.attribute = '{http://schemas.android.com/apk/res/android}'
        self.root = ET.Element('manifest')
        self.sdk = ET.SubElement(self.root, 'uses-sdk', {
            self.attribute + 'minSdkVersion': str(self.manifest['androidApi']),
            self.attribute + 'targetSdkVersion': str(self.manifest['targetApi']),
        })
        self.application = ET.SubElement(self.root, 'application', {
            self.attribute + 'extractNativeLibs': 'true', self.attribute + 'hasCode': 'true',
        })

    def validate(self):
        ET.ElementTree(self.root).write(self.path, encoding='utf-8')
        return validate_android_manifest(self.path, self.manifest)

    def test_accepts_pinned_native_launcher_manifest(self):
        self.assertEqual(self.validate().getroot().tag, 'manifest')

    def test_rejects_minimum_api_drift(self):
        self.sdk.set(self.attribute + 'minSdkVersion', '21')
        with self.assertRaisesRegex(ValueError, 'minimum API'):
            self.validate()

    def test_rejects_target_api_drift(self):
        self.sdk.set(self.attribute + 'targetSdkVersion', '28')
        with self.assertRaisesRegex(ValueError, 'target API'):
            self.validate()

    def test_rejects_missing_sdk(self):
        self.root.remove(self.sdk)
        with self.assertRaisesRegex(ValueError, 'minimum API'):
            self.validate()

    def test_rejects_disabled_library_extraction(self):
        self.application.set(self.attribute + 'extractNativeLibs', 'false')
        with self.assertRaisesRegex(ValueError, 'extractNativeLibs'):
            self.validate()

    def test_rejects_missing_application(self):
        self.root.remove(self.application)
        with self.assertRaisesRegex(ValueError, 'extractNativeLibs'):
            self.validate()

    def test_rejects_disabled_java_launcher(self):
        self.application.set(self.attribute + 'hasCode', 'false')
        with self.assertRaisesRegex(ValueError, 'hasCode'):
            self.validate()


if __name__ == '__main__':
    unittest.main()
