"""Match semantic fixture inputs and verify native reuse transport identities."""
import json
from pathlib import Path
import uuid


def validated_request(evidence: Path, fixture: dict):
    request = json.loads((evidence / 'request.json').read_text())
    semantic = dict(request)
    additions = set(request) - set(fixture)
    transport = {'runId', 'exitOnComplete'}
    if additions & transport:
        if additions != transport or request.get('exitOnComplete') is not False:
            raise RuntimeError('Unexpected native reuse request transport')
        run_id = request['runId']
        if not isinstance(run_id, str) or str(uuid.UUID(run_id)) != run_id:
            raise RuntimeError('Invalid native reuse request identity')
        result = json.loads((evidence / 'result.json').read_text())
        ready = json.loads((evidence / 'ready.json').read_text())
        process = json.loads((evidence / 'process-start.json').read_text())
        if result.get('runId') != run_id or ready.get('runId') != run_id or process.get('requestRunId') != run_id:
            raise RuntimeError('Native reuse result/ready belongs to another request')
        if ready.get('held') is not False or result.get('processId') != process.get('pid'):
            raise RuntimeError('Native reuse completion or process identity is invalid')
        semantic.pop('runId')
        semantic.pop('exitOnComplete')
    if semantic != fixture:
        raise RuntimeError('Fixture inputs changed since validation')
    return request


def validated_logs(evidence: Path, request: dict):
    """Keep case assertions local; locate startup in its verified native session."""
    log = (evidence / 'launcher.log').read_text(errors='replace')
    if request.get('exitOnComplete') is not False:
        return log, log
    process = json.loads((evidence / 'process-start.json').read_text())
    session = json.loads((evidence.parent / 'process-start.json').read_text())
    if session.get('pid') != process.get('pid'):
        raise RuntimeError('Native startup log belongs to another process')
    startup = (evidence.parent / 'launcher.log').read_text(errors='replace')
    if not log or log not in startup:
        raise RuntimeError('Native case log is missing from its session')
    return log, startup
