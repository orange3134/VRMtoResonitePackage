#!/usr/bin/env python3
"""Capture parser -> conversion -> saved hierarchy using a captured Unity v2 case.

Outputs are private diagnostic artifacts. Never updates baselines or registrations.
Exit 0: covered comparison matched, 1: needs review/stage failure, 2: invalid input.
"""
import argparse
import datetime
import json
import os
from pathlib import Path
import subprocess
import sys
import importlib.util


def load_module(name):
    spec = importlib.util.spec_from_file_location(name.replace('-', '_'), Path(__file__).with_name(name + '.py'))
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


comparison = load_module('compare-converted-structure')
replay = load_module('test-unity-resolved')


def review_saved_run(directory, mapping, unity, expected, report, output, initial_pose=None):
    previous_path = directory / 'report.json'
    previous_hash = comparison.sha256(previous_path)
    previous = comparison.snapshot_check.load(previous_path)
    require = comparison.require
    require(all(previous.get(key) == report[key] for key in
                ('format', 'schemaVersion', 'caseId', 'inputPath', 'inputSha256', 'unitySha256',
                 'coverageSha256', 'conversionArguments')), 'Previous run belongs to a different input or selection')
    stages = previous['stages']
    require(previous['status'] in ('needs-review', 'matched-covered-fields') and not previous['errors'] and
            stages['conversion'] == stages['inspect'] == 'passed' and stages['capture'] == 'captured' and
            stages['parserParity'] in ('matched', 'different'), 'Previous run did not complete the required stages')
    package = Path(previous['packagePath'])
    require(comparison.sha256(package) == previous['packageSha256'], 'Saved package changed')
    evidence = comparison.snapshot_check.load(directory / 'comparison.json')
    paths = {'unity': expected, 'parser': directory / 'parser.json', 'converted': directory / 'converted.json'}
    require(all(comparison.sha256(path) == evidence['inputs'][key]['sha256'] for key, path in paths.items()),
            'Saved comparison input changed')
    converted = comparison.snapshot_check.load(paths['converted'])
    require(evidence['packageSha256'] == converted['packageSha256'] == previous['packageSha256'],
            'Saved structure belongs to a different package')
    result = comparison.compare_captures(unity, comparison.unity_compare.load_snapshot(paths['parser']),
                                         converted, paths, mapping, initial_pose)
    require(all(result['inputs'][key]['sha256'] == evidence['inputs'][key]['sha256'] for key in paths) and
            comparison.sha256(previous_path) == previous_hash and comparison.sha256(package) == previous['packageSha256'],
            'Saved conversion evidence changed during review')
    replay.write_json(output / 'comparison.json', result)
    report.update(previousRun={'path': str(previous_path), 'sha256': previous_hash},
                  packagePath=str(package), packageSha256=converted['packageSha256'], summary=result['summary'])
    report['stages'] = dict(stages, parserParity=result['parserParity']['status'], structureComparison=result['status'])
    return result['status']


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('coverage', type=Path)
    ap.add_argument('--case', required=True)
    ap.add_argument('--output', type=Path, required=True, help='new directory under .tmp_verify')
    ap.add_argument('--timeout', type=int, default=1800, help='seconds per process')
    ap.add_argument('--mapping', type=Path, help='reviewed mapping bound to this Unity capture and saved package')
    ap.add_argument('--initial-pose', type=Path, help='source neutral weights bound to this Unity capture')
    ap.add_argument('--review-run', type=Path, help='apply a mapping to outputs of a completed conversion without rebuilding')
    args = ap.parse_args(argv)
    repo = Path(__file__).resolve().parents[1]
    output = args.output.resolve()
    try:
        require = comparison.require
        coverage = comparison.snapshot_check.load(args.coverage)
        require(coverage.get('format') == 'resopon.unity-resolved-coverage' and
                coverage.get('schemaVersion') == 1, 'unsupported coverage')
        cases = [c for c in coverage['cases'] if c['id'] == args.case]
        require(len(cases) == 1 and cases[0]['status'] == 'captured', 'select exactly one captured case')
        case = cases[0]
        require(repo / '.tmp_verify' in output.parents and not output.exists(), 'output must be new under .tmp_verify')
        require(args.timeout > 0, 'timeout must be positive')
        require(not args.review_run or args.mapping or args.initial_pose, '--review-run requires --mapping or --initial-pose')
        source = Path(case['inputPath']).resolve()
        expected = Path(case['expectedPath']).resolve()
        require(source.suffix.lower() in ('.unitypackage', '.prefab'), 'unsupported input')
        arguments = case.get('conversionArguments')
        require(arguments == [] or (isinstance(arguments, list) and len(arguments) == 2 and
                isinstance(arguments[0], str) and arguments[0].lower() == '--avatar' and
                isinstance(arguments[1], str) and arguments[1]),
                'coverage must record conversionArguments: [] or ["--avatar", "name"]')
        require(not (source.suffix.lower() == '.unitypackage' and case.get('dependencyFiles')),
                'archive requires captured external dependencies; use a complete prefab input for conversion')

        def verify():
            replay.verify_project_files(case)
            require(comparison.sha256(source) == case['inputSha256'], 'input changed; recapture Unity')
            require(comparison.sha256(expected) == case['expectedSha256'], 'Unity reference changed')

        verify()
        unity = comparison.unity_compare.load_snapshot(expected)
        require(unity['schemaVersion'] == 2 and unity['prefabGuid'] == case['prefabGuid'], 'Unity selection/schema mismatch')
        output.mkdir(parents=True)
    except (ValueError, OSError, KeyError, TypeError) as error:
        print(str(error), file=sys.stderr)
        return 2
    report = {'format': 'resopon.converted-avatar-check', 'schemaVersion': 1, 'caseId': args.case,
              'inputPath': str(source), 'inputSha256': case['inputSha256'], 'conversionArguments': arguments,
              'unityPath': str(expected), 'unitySha256': case['expectedSha256'],
              'coveragePath': str(args.coverage.resolve()), 'coverageSha256': comparison.sha256(args.coverage),
              'startedUtc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
              'status': 'failed', 'stages': {s: 'not-run' for s in
                  ('parserParity', 'conversion', 'inspect', 'capture', 'structureComparison')},
              'commands': [], 'errors': []}
    if args.review_run:
        try:
            status = review_saved_run(args.review_run.resolve(), args.mapping, unity, expected, report, output, args.initial_pose)
            verify()
            report['status'] = status
        except (OSError, ValueError, KeyError, TypeError) as error:
            report['errors'].append(str(error))
        return finish(output, report)
    build = output / 'build'
    temporary = output / 'temp'
    temporary.mkdir()
    env = dict(os.environ, RESOPON_NOPAUSE='1', TEMP=str(temporary), TMP=str(temporary))

    def run(stage, command, accepted=(0,)):
        log_path = output / (stage + '.log')
        entry = {'stage': stage, 'command': [str(c) for c in command], 'log': str(log_path)}
        report['commands'].append(entry)
        if stage in report['stages']:
            report['stages'][stage] = 'failed'
        print(stage, flush=True)
        with log_path.open('xb') as log:
            result = subprocess.run(entry['command'], cwd=repo, env=env, stdout=log, stderr=subprocess.STDOUT,
                                    timeout=args.timeout, creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
        entry['exitCode'] = result.returncode
        require(result.returncode in accepted, stage + ' failed; see ' + str(log_path))
        return result.returncode

    try:
        report['gitHead'] = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=repo, text=True).strip()
        report['dirty'] = bool(subprocess.check_output(['git', 'status', '--porcelain'], cwd=repo, text=True).strip())
        for project in ('UnityReferenceDump', 'PackageRegressionDump'):
            run('build-' + project, ['dotnet', 'build', 'tests/' + project, '-c', 'Release', '-o', build])
        binaries = {name: build / (name + '.dll') for name in ('ResoPon', 'UnityReferenceDump', 'PackageRegressionDump')}
        report['binaries'] = {name: {'path': str(path), 'sha256': comparison.sha256(path)} for name, path in binaries.items()}
        report['sourceFiles'] = {str(p.relative_to(repo)): comparison.sha256(p) for p in
                                 [Path(__file__), Path(__file__).with_name('compare-converted-structure.py'),
                                  *sorted((repo / 'tests/PackageRegressionDump').glob('*.cs'))]}
        actual = output / 'parser.json'
        try:
            run('parser', ['dotnet', binaries['UnityReferenceDump'], source, expected, actual,
                           '--verify-selection', arguments[1] if arguments else ''])
            parser = comparison.unity_compare.load_snapshot(actual)
            differences = comparison.unity_compare.compare(unity, parser)
            replay.write_json(output / 'parser-comparison.json', {'differences': differences, 'equal': not differences})
            report['stages']['parserParity'] = 'different' if differences else 'matched'
        except (ValueError, OSError, subprocess.TimeoutExpired) as error:
            report['stages']['parserParity'] = 'failed'
            report['errors'].append(str(error))
        verify()
        package = None
        try:
            run('conversion', ['dotnet', binaries['ResoPon'], source, *arguments, '--output', output / 'package'])
            packages = list((output / 'package').glob('*.resonitepackage'))
            require(len(packages) == 1, 'expected one converted package')
            package = packages[0]
            report['stages']['conversion'] = 'passed'
            report['packagePath'] = str(package)
            report['packageSha256'] = comparison.sha256(package)
        except (ValueError, OSError, subprocess.TimeoutExpired) as error:
            report['stages']['conversion'] = 'failed'
            report['errors'].append(str(error))
        if package:
            try:
                run('inspect', ['dotnet', binaries['ResoPon'], '--inspect', package])
                report['stages']['inspect'] = 'passed'
            except (ValueError, OSError, subprocess.TimeoutExpired) as error:
                report['stages']['inspect'] = 'failed'
                report['errors'].append(str(error))
            structure_path = output / 'converted.json'
            run('capture', ['dotnet', binaries['PackageRegressionDump'], '--structure', package, structure_path])
            structure = comparison.snapshot_check.load(structure_path)
            slots = comparison.validate_structure(structure)
            require(structure['packageSha256'] == report['packageSha256'], 'package changed during capture')
            report['stages']['capture'] = 'captured'
            with (output / 'converted-tree.tsv').open('x', encoding='utf-8') as stream:
                stream.write('id\tparent\tactive\tpath_json\tcomponent_types_json\n')
                for slot in slots.values():
                    stream.write('\t'.join([slot['id'], slot['parent'] or '', str(slot['activeInHierarchy']),
                                            json.dumps(slot['path'], ensure_ascii=False),
                                            json.dumps([c['type'] for c in slot['components']], ensure_ascii=False)]) + '\n')
            if report['stages']['parserParity'] == 'failed':
                parser = None
            paths = {'unity': expected, 'converted': structure_path}
            if parser is not None:
                paths['parser'] = actual
            report['stages']['structureComparison'] = 'failed'
            result = comparison.compare_captures(unity, parser, structure, paths, args.mapping, args.initial_pose)
            replay.write_json(output / 'comparison.json', result)
            report['stages']['structureComparison'] = result['status']
            report['summary'] = result['summary']
        verify()
        if report['stages'].get('structureComparison') == 'matched-covered-fields' and not report['errors']:
            report['status'] = 'matched-covered-fields'
        elif not report['errors'] and report['stages'].get('capture') == 'captured':
            report['status'] = 'needs-review'
    except (ValueError, OSError, KeyError, TypeError, subprocess.SubprocessError) as error:
        report['errors'].append(str(error))
    return finish(output, report)


def finish(output, report):
    report['finishedUtc'] = datetime.datetime.now(datetime.timezone.utc).isoformat()
    replay.write_json(output / 'report.json', report)
    print(json.dumps({'status': report['status'], 'stages': report['stages'], 'report': str(output / 'report.json')}))
    return 0 if report['status'] == 'matched-covered-fields' else 1


if __name__ == '__main__':
    sys.exit(main())
