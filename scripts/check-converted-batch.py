#!/usr/bin/env python3
"""Capture and compare every inspected package in a completed local-avatar batch.

Private diagnostic output only. Differences are review items, not automatic bug
verdicts. No baseline, registry, input or earlier report is modified.
"""
import argparse
import collections
import datetime
import importlib.util
import json
from pathlib import Path
import re
import subprocess
import sys


def module(name):
    spec = importlib.util.spec_from_file_location(name.replace('-', '_'), Path(__file__).with_name(name + '.py'))
    value = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(value)
    return value


comparison = module('compare-converted-structure')
replay = module('test-unity-resolved')


def write(path, value):
    with path.open('x', encoding='utf-8') as stream:
        json.dump(value, stream, ensure_ascii=False, indent=2)
        stream.write('\n')


def check_case(case, directory, dump, timeout):
    result = {'caseId': case['caseId'], 'conversionStatus': case['status'],
              'parserStatus': case.get('unityComparison', {}).get('status', 'not-run'),
              'captureStatus': 'not-run', 'status': 'not-compared'}
    if case['status'] != 'passed':
        return dict(result, reason='Conversion/inspection did not pass', conversionError=case.get('error'))
    directory.mkdir()
    try:
        package = Path(case['outputPackage'])
        source = Path(case['inputPath'])
        require = comparison.require
        require(comparison.sha256(package) == case['packageSha256'], 'Converted package checksum changed')
        require(comparison.sha256(source) == case['inputSha256'], 'Conversion input checksum changed')
        snapshot = directory / 'converted.json'
        command = ['dotnet', str(dump), '--structure', str(package), str(snapshot)]
        with (directory / 'capture.log').open('xb') as log:
            process = subprocess.run(command, stdout=log, stderr=subprocess.STDOUT, timeout=timeout,
                                     creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
        require(process.returncode == 0, 'Structure capture failed; see capture.log')
        converted = comparison.snapshot_check.load(snapshot)
        slots = comparison.validate_structure(converted)
        require(converted['packageSha256'] == case['packageSha256'] == comparison.sha256(package),
                'Package changed during capture')
        refs = [ref for s in slots.values() for c in s['components']
                for ref in (c['bones'] or []) + list((c['rigBones'] or {}).values())]
        result.update(captureStatus='captured', snapshotPath=str(snapshot), packageSha256=case['packageSha256'],
                      convertedSlots=len(slots), boneReferenceStatuses=dict(collections.Counter(r['status'] for r in refs)))
        unity = case.get('unityComparison', {})
        if unity.get('status') not in ('matched', 'different'):
            return dict(result, reason='No verified same-input Unity/parser pair',
                        unityReason=unity.get('error', unity.get('reason', unity.get('status'))))
        if unity.get('dependencyScope'):
            return dict(result, reason='Unity/parser used external archive dependencies absent from normal conversion')
        expected, actual = Path(unity['expectedPath']), Path(unity['actualPath'])

        def verify():
            require(comparison.sha256(expected) == unity['expectedSha256'], 'Unity baseline checksum changed')
            require(comparison.sha256(actual) == unity['actualSha256'], 'Parser snapshot checksum changed')
            require(unity['inputSha256'] == case['inputSha256'] == comparison.sha256(source), 'Unity/conversion input mismatch')
            require(Path(unity['inputPath']).resolve() == source.resolve(), 'Unity/conversion input paths differ')
            coverage_path = Path(unity['sourceCoveragePath'])
            require(comparison.sha256(coverage_path) == unity['sourceCoverageSha256'], 'Coverage manifest checksum changed')
            coverage = comparison.snapshot_check.load(coverage_path)
            records = [c for c in coverage['cases'] if c['id'] == case['caseId']]
            require(len(records) == 1, 'Expected exactly one coverage record')
            require(records[0]['expectedSha256'] == unity['expectedSha256'] and
                    records[0]['inputSha256'] == case['inputSha256'], 'Coverage/input/baseline identity mismatch')
            replay.verify_project_files(records[0])

        verify()
        report = comparison.compare(comparison.unity_compare.load_snapshot(expected),
                                    comparison.unity_compare.load_snapshot(actual), converted)
        report['inputs'] = {name: {'path': str(path), 'sha256': comparison.sha256(path)} for name, path in
                           [('unity', expected), ('parser', actual), ('converted', snapshot)]}
        report['packageSha256'] = converted['packageSha256']
        verify()
        require(comparison.sha256(package) == case['packageSha256'], 'Package changed during comparison')
        target = directory / 'comparison.json'
        write(target, report)
        result.update(status=report['status'], comparisonPath=str(target), summary=report['summary'],
                      differenceKinds=dict(collections.Counter(d['kind'] for d in report['differences'])),
                      unverifiedKinds=dict(collections.Counter(d['kind'] for d in report['unverified'])))
    except (OSError, ValueError, KeyError, TypeError, subprocess.TimeoutExpired) as error:
        result.update(status='failed', error=str(error))
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('batch_report', type=Path)
    parser.add_argument('--dump', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--timeout', type=int, default=180)
    args = parser.parse_args()
    repo = Path(__file__).resolve().parents[1]
    output, batch_path, dump = args.output.resolve(), args.batch_report.resolve(), args.dump.resolve()
    batch = comparison.snapshot_check.load(batch_path)
    registry_path = batch_path.with_name('registry.json')
    registry = comparison.snapshot_check.load(registry_path)
    ids = [c['id'] for c in registry['cases']]
    if (batch.get('status') in (None, 'running', 'build-failed') or
            len(ids) != len(set(ids)) or any(not re.fullmatch(r'[a-z0-9][a-z0-9_-]*', i) for i in ids) or
            set(ids) != {c['caseId'] for c in batch['cases']} or
            len(batch['cases']) != len(ids) or comparison.sha256(registry_path) != batch['registrySha256']):
        parser.error('A completed batch with every registered case and unchanged registry is required')
    if repo / '.tmp_verify' not in output.parents or output.exists() or not dump.is_file() or args.timeout <= 0:
        parser.error('Specify an existing dump tool, positive timeout and new output under .tmp_verify')
    output.mkdir(parents=True)
    report = {'format': 'resopon.converted-batch-review', 'schemaVersion': 1, 'status': 'running',
              'batchPath': str(batch_path), 'batchSha256': comparison.sha256(batch_path),
              'conversionGitHead': batch['gitHead'], 'converterSha256': batch['converterSha256'],
              'dumpSha256': comparison.sha256(dump),
              'comparisonSourceSha256': comparison.sha256(Path(comparison.__file__)), 'cases': [],
              'limitations': ['Name/path candidates are not provenance; unmapped slots can be required support.',
                              'Saved Enabled, skin bone pruning, merged hierarchies and unsupported fields need review.',
                              'World pose, bind poses, visuals and runtime behavior need separate checks.']}
    for case in batch['cases']:
        result = check_case(case, output / case['caseId'], dump, args.timeout)
        report['cases'].append(result)
        write(output / (case['caseId'] + '.result.json'), result)
        print(case['caseId'] + ': ' + result['status'], flush=True)
    report['summary'] = dict(collections.Counter(c['status'] for c in report['cases']))
    report['status'] = 'failed' if report['summary'].get('failed') else (
        'matched-covered-fields' if all(c['status'] == 'matched-covered-fields' for c in report['cases']) else 'needs-review')
    report['finishedUtc'] = datetime.datetime.now(datetime.timezone.utc).isoformat()
    if comparison.sha256(batch_path) != report['batchSha256'] or comparison.sha256(dump) != report['dumpSha256']:
        report.update(status='failed', error='Batch report or capture binary changed during review')
    write(output / 'report.json', report)
    print(json.dumps(report['summary']))
    return 0 if report['status'] == 'matched-covered-fields' else 1


if __name__ == '__main__':
    sys.exit(main())
