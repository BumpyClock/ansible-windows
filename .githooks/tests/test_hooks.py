#!/usr/bin/env python3
"""Executable-hook fixtures only. Stubs prove dispatch/exit semantics, not app/native health."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

SOURCE = Path(__file__).resolve().parents[2]
PROFILE = 'ansible-windows'
NATIVE = PROFILE in {'ansible-windows', 'pale-blue-dot'}
BUN = PROFILE in {'grid', 'making-with-ai'}
ZERO = '0' * 40


class HookTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='hook fixtures ')
        self.addCleanup(self.temp.cleanup)
        self.base = Path(self.temp.name)
        self.repo = self.base / 'checkout with spaces'
        self.repo.mkdir()
        self.tools = self.base / 'prepared tools'
        self.tools.mkdir()
        self.log = self.base / 'commands.jsonl'
        self.cache = self.base / 'nuget cache'
        self.cache.mkdir()
        self.env = dict(os.environ, GIT_CONFIG_GLOBAL=os.devnull, GIT_CONFIG_NOSYSTEM='1',
                        STUB_LOG=str(self.log), STUB_CACHE=str(self.cache),
                        PATH=str(self.tools) + os.pathsep + os.environ['PATH'])
        # Do not inherit agent/session Git redirection into disposable fixtures.
        for key in ('GIT_DIR', 'GIT_WORK_TREE', 'GIT_INDEX_FILE', 'GIT_COMMON_DIR'):
            self.env.pop(key, None)
        self.git('init', '-q')
        self.git('config', 'user.name', 'Hook fixture')
        self.git('config', 'user.email', 'fixture@example.invalid')
        shutil.copytree(SOURCE / '.githooks', self.repo / '.githooks')
        (self.repo / 'scripts').mkdir()
        shutil.copy2(SOURCE / 'scripts/install-hooks.sh', self.repo / 'scripts/install-hooks.sh')
        if (SOURCE / '.gitattributes').exists():
            shutil.copy2(SOURCE / '.gitattributes', self.repo / '.gitattributes')
        if NATIVE:
            (self.repo / 'tools').mkdir()
            shutil.copy2(SOURCE / 'tools/Install-GitHooks.ps1', self.repo / 'tools/Install-GitHooks.ps1')
        (self.repo / '.gitignore').write_text('node_modules/\nobj/\nbin/\nartifacts/\n')
        (self.repo / 'source with spaces.txt').write_text('one\n')
        self.commit()
        self.old = self.git('rev-parse', 'HEAD').stdout.strip()
        (self.repo / 'source with spaces.txt').write_text('two\n')
        self.commit()
        self.head = self.git('rev-parse', 'HEAD').stdout.strip()
        if BUN:
            tool = 'eslint/bin/eslint.js' if PROFILE == 'grid' else '@biomejs/biome/bin/biome'
            for path in (tool, 'typescript/bin/tsc'):
                p = self.repo / 'node_modules' / path
                p.parent.mkdir(parents=True, exist_ok=True)
                p.touch()
        stub = '''#!/usr/bin/env python3
import json, os, sys
from pathlib import Path
a = sys.argv[1:]
if a == ['--version']:
    print(os.environ.get('STUB_VERSION', '1.3.14' if Path(sys.argv[0]).name == 'bun' else '10.0.100'))
    sys.exit(0)
if a == ['nuget', 'locals', 'global-packages', '--list']:
    print('global-packages: ' + os.environ['STUB_CACHE'])
    sys.exit(0)
kind = a[0] if a else ''
if Path(sys.argv[0]).name == 'bun':
    kind = 'typecheck' if any('typescript/bin/tsc' in s for s in a) else 'lint'
with open(os.environ['STUB_LOG'], 'a') as f:
    f.write(json.dumps({'kind': kind, 'args': a, 'cwd': os.getcwd()}) + '\\n')
sys.exit(59 if os.environ.get('STUB_FAIL') == kind else 0)
'''
        for command in ('bun', 'dotnet'):
            target = self.tools / command
            target.write_text(stub)
            target.chmod(0o755)
        if NATIVE:
            target = self.tools / 'uname'
            target.write_text('#!/bin/sh\necho MINGW64_NT-10.0\n')
            target.chmod(0o755)

    def run_command(self, args, **kwargs):
        return subprocess.run(args, cwd=self.repo, env=self.env, text=True,
                              stdout=subprocess.PIPE, stderr=subprocess.PIPE, **kwargs)

    def git(self, *args):
        result = self.run_command(['git', *args])
        self.assertEqual(result.returncode, 0, result.stderr)
        return result

    def commit(self):
        self.git('add', '.')  # Disposable fixture only, never the user's checkout.
        self.git('commit', '-qm', 'fixture')

    def records(self):
        return [json.loads(line) for line in self.log.read_text().splitlines()] if self.log.exists() else []

    def row(self, oid=None, remote=None, ref='refs/heads/fixture'):
        return f'{ref} {oid or self.head} {ref} {remote or self.old}\n'

    def hook(self, stdin=None):
        return self.run_command([str(self.repo / '.githooks/pre-push'), 'origin', 'unused.invalid'],
                                input=self.row() if stdin is None else stdin)

    def assert_pass(self, result):
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertTrue(self.records(), 'No project checks ran')
        self.assertTrue(all(r['cwd'] == str(self.repo) for r in self.records()))

    def test_existing_ref_runs_project_commands(self):
        self.assert_pass(self.hook())
        records = self.records()
        if BUN:
            self.assertEqual([r['kind'] for r in records], ['lint', 'typecheck'])
            self.assertIn('-b', records[-1]['args'])
        else:
            self.assertTrue(any(r['kind'] == 'test' for r in records))
            self.assertTrue(all('--source' in r['args'] for r in records if r['kind'] == 'restore'))
            if PROFILE == 'ansible-windows':
                args = next(r['args'] for r in records if r['kind'] == 'test')
                self.assertIn('Category!=NativeIntegration', args)
                self.assertFalse(any('publish' in r['args'] for r in records))
            if PROFILE == 'pale-blue-dot':
                args = [r['args'] for r in records if r['kind'] == 'test']
                self.assertEqual(len(args), 2)
                windows = args[-1]
                for arg in ('-p:WindowsPackageType=None', '-p:UapAppxPackageBuildMode=SideloadOnly',
                            '--artifacts-path', 'artifacts/windows-tests'):
                    self.assertIn(arg, windows)
                self.assertNotIn('--filter', windows)

    def test_new_branch_runs_full_checks(self):
        self.assert_pass(self.hook(self.row(remote=ZERO)))

    def test_unknown_base_runs_full_checks(self):
        self.assert_pass(self.hook(self.row(remote='f' * 40)))

    def test_empty_stdin_runs_full_checks(self):
        self.assert_pass(self.hook(''))

    def test_multiple_refs_run_checks_once(self):
        self.assert_pass(self.hook(self.row() + self.row(ref='refs/heads/second')))
        expected_tests = 2 if PROFILE == 'pale-blue-dot' else 1
        self.assertEqual(sum(r['kind'] == ('typecheck' if BUN else 'test') for r in self.records()), expected_tests)

    def test_non_head_ref_refused_before_any_checks(self):
        result = self.hook(self.row() + self.row(oid=self.old, ref='refs/heads/other'))
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('not checked-out HEAD', result.stderr)
        self.assertEqual(self.records(), [])

    def test_deletion_only_skips_checks_even_if_dirty(self):
        (self.repo / 'untracked').touch()
        result = self.hook(self.row(oid=ZERO))
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.records(), [])

    def test_mixed_deletion_and_update_checks(self):
        self.assert_pass(self.hook(self.row(oid=ZERO) + self.row()))

    def test_malformed_stdin_refused(self):
        for data in ('garbage\n', '\n', self.row().replace(self.head, 'x' * 40), self.row().rstrip() + ' extra\n'):
            with self.subTest(data=data):
                self.assertNotEqual(self.hook(data).returncode, 0)
        self.assertEqual(self.records(), [])

    def test_final_record_without_newline_is_validated(self):
        self.assert_pass(self.hook(self.row().rstrip('\n')))
        self.assertNotEqual(self.hook('garbage').returncode, 0)

    def test_git_status_failure_is_not_a_clean_checkout(self):
        real_git = shutil.which('git', path=os.environ['PATH'])
        wrapper = self.tools / 'git'
        wrapper.write_text('#!/bin/sh\nif [ "$1" = status ]; then exit 73; fi\nexec ' + json.dumps(real_git) + ' "$@"\n')
        wrapper.chmod(0o755)
        self.assertEqual(self.hook().returncode, 73)
        self.assertEqual(self.records(), [])

    def test_dirty_checkout_refused_without_mutation(self):
        path = self.repo / 'source with spaces.txt'
        path.write_text('user changes\n')
        self.assertNotEqual(self.hook().returncode, 0)
        self.assertEqual(path.read_text(), 'user changes\n')
        self.assertEqual(self.records(), [])

    def test_untracked_checkout_refused(self):
        (self.repo / 'untracked').touch()
        self.assertNotEqual(self.hook().returncode, 0)
        self.assertEqual(self.records(), [])

    def test_rename_and_delete_with_spaces_run_full_checks(self):
        self.git('mv', 'source with spaces.txt', 'renamed with spaces.txt')
        self.commit()
        self.head = self.git('rev-parse', 'HEAD').stdout.strip()
        self.assert_pass(self.hook())
        self.git('rm', 'renamed with spaces.txt')
        self.commit()
        self.head = self.git('rev-parse', 'HEAD').stdout.strip()
        self.assert_pass(self.hook())

    def test_shared_configuration_runs_full_checks(self):
        (self.repo / ('package.json' if BUN else 'Directory.Build.props')).write_text('{}\n')
        self.commit()
        self.head = self.git('rev-parse', 'HEAD').stdout.strip()
        self.assert_pass(self.hook())

    def test_command_failure_is_propagated_and_stops_checks(self):
        self.env['STUB_FAIL'] = 'lint' if BUN else 'restore'
        result = self.hook()
        self.assertEqual(result.returncode, 59, result.stdout + result.stderr)
        self.assertEqual([r['kind'] for r in self.records()], [self.env['STUB_FAIL']])

    def test_final_command_failure_is_propagated(self):
        self.env['STUB_FAIL'] = 'typecheck' if BUN else 'test'
        result = self.hook()
        self.assertEqual(result.returncode, 59, result.stdout + result.stderr)

    def test_wrong_toolchain_fails_without_download(self):
        self.env['STUB_VERSION'] = '0.0.0'
        self.assertNotEqual(self.hook().returncode, 0)
        self.assertEqual(self.records(), [])

    def test_installer_is_opt_in_and_idempotent(self):
        self.assertNotEqual(self.run_command(['git', 'config', '--get', 'core.hooksPath']).returncode, 0)
        for _ in range(2):
            result = self.run_command(['sh', 'scripts/install-hooks.sh'])
            self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.git('config', '--local', '--get', 'core.hooksPath').stdout.strip(), '.githooks')
        self.assert_pass(self.hook())

    def test_installer_preserves_other_hooks_path(self):
        self.git('config', 'core.hooksPath', 'existing hooks')
        result = self.run_command(['sh', 'scripts/install-hooks.sh'])
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.git('config', '--get', 'core.hooksPath').stdout.strip(), 'existing hooks')

    def test_installer_preserves_explicit_empty_hooks_path(self):
        self.git('config', 'core.hooksPath', '')
        result = self.run_command(['sh', 'scripts/install-hooks.sh'])
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.git('config', '--get', 'core.hooksPath').stdout.strip(), '')

    def test_installer_preserves_existing_default_hook(self):
        path = self.repo / '.git/hooks/pre-commit'
        path.write_text('#!/bin/sh\necho existing\n')
        result = self.run_command(['sh', 'scripts/install-hooks.sh'])
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('echo existing', path.read_text())
        self.assertNotEqual(self.run_command(['git', 'config', '--get', 'core.hooksPath']).returncode, 0)

    def test_autocrlf_checkout_preserves_shell_entrypoints(self):
        self.git('config', 'core.autocrlf', 'true')
        self.git('checkout-index', '--force', '--all')
        for relative in ['.githooks/pre-push', 'scripts/install-hooks.sh', '.githooks/check.sh']:
            path = self.repo / relative
            if path.exists():
                self.assertNotIn(b'\r\n', path.read_bytes(), relative)
        result = self.run_command(['sh', 'scripts/install-hooks.sh'])
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_installer_preserves_symlinked_default_hooks(self):
        hooks = self.repo / '.git/hooks'
        shutil.rmtree(hooks)  # Disposable fixture only.
        external = self.base / 'existing hooks'
        external.mkdir()
        hook = external / 'pre-commit'
        hook.write_text('#!/bin/sh\nexit 23\n')
        hook.chmod(0o755)
        hooks.symlink_to(external, target_is_directory=True)
        self.assertEqual(self.run_command(['git', 'hook', 'run', 'pre-commit']).returncode, 23)
        result = self.run_command(['sh', 'scripts/install-hooks.sh'])
        self.assertNotEqual(result.returncode, 0)
        self.assertNotEqual(self.run_command(['git', 'config', '--get', 'core.hooksPath']).returncode, 0)
        self.assertEqual(self.run_command(['git', 'hook', 'run', 'pre-commit']).returncode, 23)

    def test_installer_refuses_failed_hook_directory_scan(self):
        wrapper = self.tools / 'find'
        wrapper.write_text('#!/bin/sh\nexit 74\n')
        wrapper.chmod(0o755)
        result = self.run_command(['sh', 'scripts/install-hooks.sh'])
        self.assertEqual(result.returncode, 74)
        self.assertNotEqual(self.run_command(['git', 'config', '--get', 'core.hooksPath']).returncode, 0)

    def test_linked_worktree_uses_common_hooks_directory(self):
        linked = self.base / 'linked checkout'
        self.git('worktree', 'add', '-qb', 'linked-fixture', str(linked))
        (self.repo / '.git/hooks/pre-commit').write_text('#!/bin/sh\nexit 0\n')
        result = subprocess.run(['sh', 'scripts/install-hooks.sh'], cwd=linked, env=self.env,
                                text=True, capture_output=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('existing hooks', result.stderr)

    @unittest.skipUnless(NATIVE, 'Windows wrapper only')
    def test_powershell_installer_wrapper(self):
        result = self.run_command(['pwsh', '-NoLogo', '-NoProfile', '-File', 'tools/Install-GitHooks.ps1'])
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(self.git('config', '--get', 'core.hooksPath').stdout.strip(), '.githooks')


if __name__ == '__main__':
    unittest.main(verbosity=2)
