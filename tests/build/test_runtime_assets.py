"""Exercise the real native build guards without native workloads or downloaded Pyodide.

Run: python3 -B -m unittest discover -s tests/build
"""
import pathlib
import subprocess
import tempfile
import unittest
import xml.etree.ElementTree as ET


class RuntimeAssetsTests(unittest.TestCase):
    names = ['pyodide.mjs', 'pyodide.asm.mjs', 'pyodide.asm.wasm',
             'python_stdlib.zip', 'pyodide-lock.json']

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='brilliant-runtime-assets-')
        self.addCleanup(self.temp.cleanup)
        self.root = pathlib.Path(self.temp.name)
        self.legacy = self.root / 'src/Brilliant.App/wwwroot/pyodide'
        self.runtime = self.root / 'src/Brilliant.Runtime.Python/wwwroot/pyodide'
        self.runtime.mkdir(parents=True)
        self.project = self.root / 'test.proj'
        project = ET.Element('Project')
        properties = ET.SubElement(project, 'PropertyGroup')
        ET.SubElement(properties, 'AppRepositoryRoot').text = self.root.as_posix() + '/'
        ET.SubElement(project, 'Target', Name='SelectAppRuntimes')
        ET.SubElement(project, 'Target', Name='PrepareAppFlavor')
        targets = pathlib.Path(__file__).resolve().parents[2] / 'src/Brilliant.App/RuntimeAssets.targets'
        ET.SubElement(project, 'Import', Project=str(targets))
        ET.ElementTree(project).write(self.project, encoding='unicode')

    def run_build(self, python=True, target='SelectAppRuntimes'):
        return subprocess.run(['dotnet', 'msbuild', str(self.project), '-nologo',
                               f'-t:{target}', f'-p:AppPythonEnabled={str(python).lower()}'],
                              capture_output=True, text=True)

    def install(self):
        for name in self.names:
            (self.runtime / name).write_text('test asset')

    def test_python_requires_each_of_the_five_files(self):
        self.install()
        for name in self.names:
            with self.subTest(name=name):
                path = self.runtime / name
                path.unlink()
                result = self.run_build()
                self.assertNotEqual(0, result.returncode)
                self.assertIn('Missing Python runtime asset', result.stdout)
                self.assertIn(name, result.stdout)
                path.write_text('test asset')

    def test_python_build_accepts_assets_in_the_new_location(self):
        self.install()
        result = self.run_build()
        self.assertEqual(0, result.returncode, result.stdout)

    def test_no_runtime_needs_no_pyodide_directory(self):
        self.runtime.rmdir()
        result = self.run_build(python=False)
        self.assertEqual(0, result.returncode, result.stdout)

    def test_legacy_assets_block_every_flavor_with_migration_advice(self):
        self.install()
        self.legacy.mkdir(parents=True)
        (self.legacy / 'pyodide.asm.wasm').write_text('old download')
        for python in [True, False]:
            for target in ['SelectAppRuntimes', 'PrepareAppFlavor']:
                with self.subTest(python=python, target=target):
                    result = self.run_build(python, target)
                    self.assertNotEqual(0, result.returncode)
                    self.assertIn('Move these to src/Brilliant.Runtime.Python/wwwroot/pyodide/', result.stdout)

    def test_legacy_placeholder_is_allowed(self):
        self.legacy.mkdir(parents=True)
        (self.legacy / '.gitkeep').touch()
        result = self.run_build(python=False)
        self.assertEqual(0, result.returncode, result.stdout)
