import json
from datetime import datetime, timedelta, timezone
import pytest
import zstandard

from session_manager.local_usage import LocalUsageReader
from session_manager.model_pricing import LocalModelPricing

SHANGHAI = timezone(timedelta(hours=8))


def event(kind, payload, timestamp=None):
    value = {'type': kind, 'payload': payload}
    if timestamp:
        value['timestamp'] = timestamp
    return json.dumps(value)


def token_count(input_tokens, cached_tokens, output_tokens):
    return event('event_msg', {'type': 'token_count', 'info': {
        'last_token_usage': {'input_tokens': input_tokens,
                             'cached_input_tokens': cached_tokens,
                             'output_tokens': output_tokens}}})


def test_reads_codex_token_counts_and_filters_by_model_and_period(tmp_path):
    home = tmp_path / 'codex'
    rollout = home / 'sessions' / '2026' / '11' / '02' / 'rollout.jsonl'
    rollout.parent.mkdir(parents=True)
    first = datetime.now(SHANGHAI).replace(hour=10, minute=2, second=0, microsecond=0)
    second = first + timedelta(minutes=5)
    lines = [
        event('session_meta', {'model_provider': 'custom'}, first.isoformat()),
        event('turn_context', {'model': 'gpt-test'}, first.isoformat()),
        token_count(1000, 870, 40),
        event('turn_context', {'model': 'gpt-mini'}, second.isoformat()),
        token_count(500, 250, 20),
    ]
    lines[2] = json.dumps({'timestamp': first.isoformat(), **json.loads(lines[2])})
    lines[4] = json.dumps({'timestamp': second.isoformat(), **json.loads(lines[4])})
    rollout.write_text('\n'.join(lines) + '\n', encoding='utf-8')

    reader = LocalUsageReader()
    all_models = reader.read(home, 'today', now=first)
    assert all_models['totals']['input'] == 1500
    assert all_models['totals']['read'] == 1120
    assert all_models['totals']['output'] == 60
    assert all_models['totals']['records'] == 2
    assert all_models['totals']['tokenRate'] == 1120 / 1500
    assert all(row['statusCode'] is None for row in all_models['logs']), 'usage events without HTTP status must stay unknown'
    assert {row['name'] for row in all_models['models']} == {'gpt-test', 'gpt-mini'}
    assert reader.read(home, 'today', 'gpt-mini', now=first)['totals']['records'] == 1
    assert set(reader.read(home, 'today', 'gpt-mini', now=first)['modelNames']) == {'gpt-test', 'gpt-mini'}


@pytest.mark.parametrize('location,field,value,expected', [
    ('info', 'status_code', 200, 200),
    ('payload', 'http_status', '429', 429),
    ('event', 'http_status_code', 503, 503),
    ('last', 'statusCode', 302, 302),
    ('info', 'response', {'status': 201}, 201),
    ('payload', 'http_response', {'status_code': '500'}, 500),
    ('info', 'code', 200, None),
    ('info', 'status', 'success', None),
    ('info', 'status_code', True, None),
    ('info', 'status_code', 200.5, None),
    ('info', 'status_code', 600, None),
    ('info', 'status_code', '200 OK', None),
])
def test_uses_only_explicit_valid_http_status_from_the_same_event(tmp_path, location, field, value, expected):
    rollout = tmp_path / 'sessions' / 'rollout.jsonl'
    rollout.parent.mkdir()
    now = datetime.now(SHANGHAI)
    message = json.loads(token_count(100, 80, 10))
    message['timestamp'] = now.isoformat()
    sources = {'event': message, 'payload': message['payload'],
               'info': message['payload']['info'],
               'last': message['payload']['info']['last_token_usage']}
    sources[location][field] = value
    rollout.write_text(json.dumps(message) + '\n', encoding='utf-8')
    result = LocalUsageReader().read(tmp_path, now=now)
    assert result['totals']['records'] == 1
    assert result['logs'][0]['statusCode'] == expected


def test_incremental_append_does_not_double_count_existing_events(tmp_path):
    home = tmp_path / 'codex'
    rollout = home / 'sessions' / 'rollout.jsonl'
    rollout.parent.mkdir(parents=True)
    now = datetime.now(SHANGHAI)
    prefix = [event('turn_context', {'model': 'gpt-test'})]
    token = json.dumps({'timestamp': now.isoformat(), **json.loads(token_count(100, 80, 10))})
    rollout.write_text('\n'.join(prefix + [token]) + '\n', encoding='utf-8')
    reader = LocalUsageReader()

    assert reader.read(home, 'today', now=now)['totals']['records'] == 1
    with rollout.open('a', encoding='utf-8') as stream:
        stream.write(token + '\n')
    result = reader.read(home, 'today', now=now)
    assert result['totals']['records'] == 2
    assert result['totals']['read'] == 160


def test_unusable_or_non_token_events_do_not_create_usage(tmp_path):
    home = tmp_path / 'codex'
    rollout = home / 'archived_sessions' / 'rollout.jsonl'
    rollout.parent.mkdir(parents=True)
    now = datetime.now(SHANGHAI)
    invalid = event('event_msg', {'type': 'token_count', 'info': {
        'last_token_usage': {'input_tokens': 10, 'cached_input_tokens': 11, 'output_tokens': 2}}}, now.isoformat())
    rollout.write_text(event('event_msg', {'type': 'agent_message', 'message': 'not usage'}) + '\n' + invalid + '\n', encoding='utf-8')

    result = LocalUsageReader().read(home, 'today', now=now)
    assert result['totals']['records'] == 0
    assert result['logs'] == []


def usage_event(timestamp, total, last=None):
    info = {'total_token_usage': total}
    if last is not None:
        info['last_token_usage'] = last
    return event('event_msg', {'type': 'token_count', 'info': info}, timestamp)


def values(input_tokens, cached, output, write=None):
    result = {'input_tokens': input_tokens, 'cached_input_tokens': cached, 'output_tokens': output}
    if write is not None:
        result['cache_write_input_tokens'] = write
    return result


def test_repeated_cumulative_usage_is_not_counted_twice_after_append(tmp_path, monkeypatch):
    home = tmp_path
    path = home / 'sessions' / 'rollout.jsonl'
    path.parent.mkdir()
    now = datetime(2026, 11, 2, 12, tzinfo=SHANGHAI)
    first = values(100, 80, 10, 5)
    path.write_text(usage_event(now.isoformat(), first, first) + '\n', encoding='utf-8')
    reader = LocalUsageReader()
    assert reader.read(home, now=now)['totals']['input'] == 100
    offset = reader.files[str(path)]['offset']
    calls = []
    original = reader._record
    monkeypatch.setattr(reader, '_record', lambda *args: (calls.append(1), original(*args))[1])
    with path.open('a', encoding='utf-8') as stream:
        stream.write(usage_event((now+timedelta(seconds=1)).isoformat(), first, first) + '\n')
        stream.write(usage_event((now+timedelta(seconds=2)).isoformat(), values(200, 160, 20, 10), first) + '\n')
    result = reader.read(home, now=now)
    assert len(calls) == 2, 'small growing files should read only appended lines'
    assert reader.files[str(path)]['offset'] > offset
    assert result['totals']['records'] == 2
    assert result['totals']['input'] == 200
    assert result['totals']['read'] == 160
    assert result['totals']['write'] == 10


def test_cumulative_fallback_reset_and_beijing_date_boundary(tmp_path):
    path = tmp_path / 'sessions' / 'rollout.jsonl'
    path.parent.mkdir()
    path.write_text('\n'.join([
        usage_event('2026-11-01T15:59:59Z', values(100, 80, 10)),
        usage_event('2026-11-01T16:00:01Z', values(150, 100, 15)),
        usage_event('2026-11-01T16:00:02Z', values(20, 10, 2)),
    ])+'\n', encoding='utf-8')
    now = datetime(2026, 11, 2, 12, tzinfo=SHANGHAI)
    reader = LocalUsageReader()
    today = reader.read(tmp_path, now=now)
    assert today['totals']['input'] == 70
    assert today['totals']['read'] == 30
    assert today['totals']['output'] == 7
    assert today['totals']['write'] is None
    assert reader.read(tmp_path, 'yesterday', now=now)['totals']['input'] == 100
    assert reader.read(tmp_path, 'range:2026-11-01:2026-11-02', now=now)['totals']['input'] == 170
    with pytest.raises(ValueError):
        reader.read(tmp_path, 'range:2026-01-01:2026-11-02', now=now)


def test_partial_line_rewrite_removal_and_archived_compressed_copy(tmp_path):
    path = tmp_path / 'sessions' / 'rollout.jsonl'
    path.parent.mkdir()
    now = datetime.now(SHANGHAI)
    header = event('session_meta', {'id': 'same-session'})+'\n'
    token = usage_event(now.isoformat(), values(100, 80, 10))
    path.write_text(header+token[:20], encoding='utf-8')
    reader = LocalUsageReader()
    assert reader.read(tmp_path, now=now)['totals']['records'] == 0
    with path.open('a', encoding='utf-8') as stream:
        stream.write(token[20:]+'\n')
    assert reader.read(tmp_path, now=now)['totals']['records'] == 1
    archived = tmp_path / 'archived_sessions' / 'rollout.jsonl.zst'
    archived.parent.mkdir()
    archived.write_bytes(zstandard.ZstdCompressor().compress(path.read_bytes()))
    assert reader.read(tmp_path, now=now)['totals']['records'] == 1, 'copied session is not double counted'
    path.write_text(header+usage_event(now.isoformat(), values(200, 160, 20))+'\n', encoding='utf-8')
    archived.unlink()
    assert reader.read(tmp_path, now=now)['totals']['input'] == 200
    path.unlink()
    assert reader.read(tmp_path, now=now)['totals']['records'] == 0
    assert not reader.files


def test_recent_records_are_bounded_without_truncating_totals(tmp_path):
    path = tmp_path / 'sessions' / 'rollout.jsonl'
    path.parent.mkdir()
    now = datetime.now(SHANGHAI).replace(hour=10, minute=0, second=0, microsecond=0)
    path.write_text('\n'.join(usage_event((now+timedelta(seconds=i)).isoformat(), values((i+1)*100, (i+1)*80, (i+1)*10)) for i in range(800))+'\n', encoding='utf-8')
    reader = LocalUsageReader()
    result = reader.read(tmp_path, now=now)
    assert result['totals']['records'] == 800
    assert result['totals']['input'] == 80000
    assert len(result['logs']) == 300
    assert len(reader.files[str(path)]['buckets']) == 1


def test_corrupt_compressed_session_reports_incomplete_statistics(tmp_path):
    path = tmp_path / 'sessions' / 'rollout.jsonl.zst'
    path.parent.mkdir()
    path.write_bytes(b'not a zstd stream')
    result = LocalUsageReader().read(tmp_path)
    assert result['warnings']
    assert result['sync']['errors'] == 1
    assert result['sync']['scannedFiles'] == 1


def test_sync_counts_initial_append_filters_and_duplicate_archives(tmp_path):
    path = tmp_path / 'sessions' / 'rollout.jsonl'
    path.parent.mkdir()
    now = datetime.now(SHANGHAI)
    path.write_text(event('session_meta', {'id': 'sync-session'})+'\n'+
                    usage_event(now.isoformat(), values(100, 80, 10))+'\n', encoding='utf-8')
    reader = LocalUsageReader()
    assert reader.read(tmp_path, now=now)['sync'] == {
        'initial': True, 'added': 1, 'scannedFiles': 1, 'errors': 0}
    assert reader.read(tmp_path, '7', 'missing-model', now=now)['sync'] == {
        'initial': False, 'added': 0, 'scannedFiles': 1, 'errors': 0}
    with path.open('a', encoding='utf-8') as stream:
        stream.write(usage_event(now.isoformat(), values(200, 160, 20))+'\n')
    assert reader.read(tmp_path, now=now)['sync']['added'] == 1
    archived = tmp_path / 'archived_sessions' / 'rollout.jsonl'
    archived.parent.mkdir()
    archived.write_bytes(path.read_bytes())
    assert reader.read(tmp_path, now=now)['sync'] == {
        'initial': False, 'added': 0, 'scannedFiles': 2, 'errors': 0}


def test_sync_keeps_previous_counts_when_a_file_temporarily_fails(tmp_path, monkeypatch):
    path = tmp_path / 'sessions' / 'rollout.jsonl'
    path.parent.mkdir()
    now = datetime.now(SHANGHAI)
    path.write_text(usage_event(now.isoformat(), values(100, 80, 10))+'\n', encoding='utf-8')
    reader = LocalUsageReader()
    reader.read(tmp_path, now=now)
    original = reader._parse_plain
    monkeypatch.setattr(reader, '_parse_plain', lambda *args: (_ for _ in ()).throw(OSError('locked')))
    with path.open('a', encoding='utf-8') as stream:
        stream.write(usage_event(now.isoformat(), values(200, 160, 20))+'\n')
    assert reader.read(tmp_path, now=now)['sync']['errors'] == 1
    monkeypatch.setattr(reader, '_parse_plain', original)
    assert reader.read(tmp_path, now=now)['sync']['added'] == 1


def write_prices(path, *models, rates=('2', '10', '0.1', '2.5')):
    fields = ('inputCostPerMillion', 'outputCostPerMillion',
              'cacheReadCostPerMillion', 'cacheCreationCostPerMillion')
    path.write_text(json.dumps({'models': [dict(modelId=model, **dict(zip(fields, rates)))
                                          for model in models]}), encoding='utf-8')


def priced_reader(tmp_path):
    return LocalUsageReader(pricing=LocalModelPricing(tmp_path/'no-cc-switch.json'))


def test_local_cost_counts_cached_tokens_once_and_retains_known_writes(tmp_path):
    path = tmp_path/'sessions'/'rollout.jsonl'
    path.parent.mkdir()
    write_prices(tmp_path/'relay-model-pricing.json', 'priced-model')
    now = datetime(2026, 11, 2, 12, tzinfo=SHANGHAI)
    first = values(1_000_000, 800_000, 50_000, 100_000)
    first['reasoning_output_tokens'] = 40_000
    # Mixing known and missing writes in an hourly bucket must not discard the
    # known write charge, even though the display's write total is unknown.
    second = values(1_000_000, 800_000, 50_000)
    path.write_text('\n'.join([
        event('turn_context', {'model': 'priced-model'}),
        usage_event(now.isoformat(), first, first),
        usage_event((now+timedelta(seconds=1)).isoformat(), values(2_000_000, 1_600_000, 100_000), second),
    ])+'\n', encoding='utf-8')
    reader = priced_reader(tmp_path)
    result = reader.read(tmp_path, now=now)
    assert result['totals']['cost'] == pytest.approx(1.03 + .98)
    assert result['totals']['write'] is None
    assert sum(row['cost'] for row in result['logs']) == pytest.approx(result['totals']['cost'])
    assert result['models'][0]['cost'] == pytest.approx(2.01)
    assert result['daily'][0]['cost'] == pytest.approx(2.01)
    assert result['pricing'] == {'source': '本地自定义价格', 'currency': 'USD',
                                 'estimated': True, 'missingModels': []}
    assert reader.read(tmp_path, now=now)['totals']['cost'] == pytest.approx(2.01)


def test_cost_missing_prices_filters_empty_ranges_and_free_models(tmp_path):
    path = tmp_path/'sessions'/'rollout.jsonl'
    path.parent.mkdir()
    write_prices(tmp_path/'relay-model-pricing.json', 'free-model', rates=('0',)*4)
    now = datetime(2026, 11, 2, 12, tzinfo=SHANGHAI)
    lines = []
    for name in ('free-model', 'unpriced-model', 'gpt-6.1-sol-custom'):
        lines.extend([event('turn_context', {'model': name}),
                      event('token_count', values(100, 80, 10), now.isoformat())])
    path.write_text('\n'.join(lines)+'\n', encoding='utf-8')
    reader = priced_reader(tmp_path)
    result = reader.read(tmp_path, now=now)
    assert result['totals']['cost'] is None
    assert result['pricing']['missingModels'] == ['gpt-6.1-sol-custom', 'unpriced-model']
    assert reader.read(tmp_path, model='free-model', now=now)['totals']['cost'] == 0
    assert reader.read(tmp_path, model='unpriced-model', now=now)['totals']['cost'] is None
    yesterday = reader.read(tmp_path, 'yesterday', now=now)
    assert yesterday['totals']['cost'] == 0
    assert yesterday['pricing']['missingModels'] == []


def test_price_changes_recalculate_cached_usage_without_rescanning(tmp_path, monkeypatch):
    path = tmp_path/'sessions'/'rollout.jsonl'
    path.parent.mkdir()
    now = datetime.now(SHANGHAI)
    path.write_text(event('turn_context', {'model': 'priced-model'})+'\n'+
                    event('token_count', values(1_000_000, 800_000, 50_000), now.isoformat())+'\n', encoding='utf-8')
    prices = tmp_path/'relay-model-pricing.json'
    write_prices(prices, 'priced-model')
    reader = priced_reader(tmp_path)
    assert reader.read(tmp_path, now=now)['totals']['cost'] == pytest.approx(.98)
    monkeypatch.setattr(reader, '_parse_plain', lambda *args: pytest.fail('unchanged transcript was rescanned'))
    write_prices(prices, 'priced-model', rates=('4', '20', '0.2', '5'))
    result = reader.read(tmp_path, now=now)
    assert result['totals']['cost'] == pytest.approx(1.96)
    assert result['sync']['added'] == 0


def test_cc_switch_price_fallback_custom_precedence_and_invalid_prices(tmp_path):
    cc_prices = tmp_path/'cc-model-pricing.json'
    write_prices(cc_prices, 'gpt-6.1-sol', rates=('4', '20', '.2', '5'))
    pricing = LocalModelPricing(cc_prices).load(tmp_path)
    row = {'model': 'GPT-6.1-SOL', 'input': 1_000_000, 'read': 800_000, 'output': 50_000}
    assert pricing.source == 'CC Switch 本地价格'
    assert pricing.estimate(row) == pytest.approx(1.96)
    custom = tmp_path/'relay-model-pricing.json'
    write_prices(custom, 'gpt-6.1-sol', rates=('0',)*4)
    assert pricing.load(tmp_path).estimate(row) == 0
    write_prices(custom, 'gpt-6.1-sol', rates=('2', '10', 'NaN', '2.5'))
    assert pricing.load(tmp_path).estimate(row) is None
    custom.write_text('{bad json', encoding='utf-8')
    assert pricing.load(tmp_path).estimate(row) == pytest.approx(1.96)
    assert pricing.warnings
    cc_prices.unlink()
    custom.unlink()
    assert pricing.load(tmp_path).estimate(row) == pytest.approx(.98)
