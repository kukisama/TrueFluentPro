#!/usr/bin/env python3
"""
APIM AI API 部署脚本 — 在 ARM deploymentScript (AzureCLI) 容器内运行
下载原始 OpenAPI 3.1 spec → 预处理 → REST 部署 backend/API/policy/custom ops/tags
"""
import json, os, subprocess, sys, time
from urllib.request import urlopen

# ── 从 ARM environmentVariables 读取 ──
SPEC_URL     = os.environ['SPEC_URL']
SUB_ID       = os.environ['SUB_ID']
RG           = os.environ['RG_NAME']
SVC          = os.environ['SVC_NAME']
API_NAME     = os.environ['API_NAME']
DISPLAY_NAME = os.environ.get('DISPLAY_NAME') or API_NAME
API_PATH     = os.environ.get('API_PATH') or f'{API_NAME}/openai'
BACKEND_URL  = os.environ['BACKEND_URL']
BACKEND_ID   = os.environ.get('BACKEND_ID') or f'{API_NAME}-ai-endpoint'

AV   = '2023-09-01-preview'
BASE = f'/subscriptions/{SUB_ID}/resourceGroups/{RG}/providers/Microsoft.ApiManagement/service/{SVC}'
MGMT = 'https://management.azure.com'

# ── 15 个自定义操作 (等价于 custom_operations.json) ──
CUSTOM_OPS = [
    {"name": "Completions_Create", "tag": "Completions", "props": {
        "displayName": "Creates a completion for the provided prompt, parameters and chosen model.",
        "method": "POST",
        "urlTemplate": "/deployments/{deployment-id}/completions?api-version={api-version}",
        "templateParameters": [
            {"name": "deployment-id", "type": "string", "required": True},
            {"name": "api-version", "type": "string", "required": True}],
        "description": "Creates a completion for the provided prompt, parameters and chosen model."}},
    {"name": "Embeddings_Create", "tag": "Embeddings", "props": {
        "displayName": "Get a vector representation of a given input.",
        "method": "POST",
        "urlTemplate": "/deployments/{deployment-id}/embeddings?api-version={api-version}",
        "templateParameters": [
            {"name": "deployment-id", "type": "string", "required": True},
            {"name": "api-version", "type": "string", "required": True}],
        "description": "Get a vector representation of a given input that can be easily consumed by machine learning models and algorithms."}},
    {"name": "ChatCompletions_Create", "tag": "Chat", "props": {
        "displayName": "Creates a completion for the chat message",
        "method": "POST",
        "urlTemplate": "/deployments/{deployment-id}/chat/completions?api-version={api-version}",
        "templateParameters": [
            {"name": "deployment-id", "type": "string", "required": True},
            {"name": "api-version", "type": "string", "required": True}],
        "description": "Creates a completion for the chat message."}},
    {"name": "Transcriptions_Create", "tag": "Audio", "props": {
        "displayName": "Transcribes audio into the input language.",
        "method": "POST",
        "urlTemplate": "/deployments/{deployment-id}/audio/transcriptions?api-version={api-version}",
        "templateParameters": [
            {"name": "deployment-id", "type": "string", "required": True},
            {"name": "api-version", "type": "string", "required": True}],
        "description": "Transcribes audio into the input language."}},
    {"name": "Translations_Create", "tag": "Audio", "props": {
        "displayName": "Transcribes and translates input audio into English text.",
        "method": "POST",
        "urlTemplate": "/deployments/{deployment-id}/audio/translations?api-version={api-version}",
        "templateParameters": [
            {"name": "deployment-id", "type": "string", "required": True},
            {"name": "api-version", "type": "string", "required": True}],
        "description": "Transcribes and translates input audio into English text."}},
    {"name": "Speech_Create", "tag": "Audio", "props": {
        "displayName": "Generates audio from the input text.",
        "method": "POST",
        "urlTemplate": "/deployments/{deployment-id}/audio/speech?api-version={api-version}",
        "templateParameters": [
            {"name": "deployment-id", "type": "string", "required": True},
            {"name": "api-version", "type": "string", "required": True}],
        "description": "Generates audio from the input text."}},
    {"name": "ImageGenerations_Create", "tag": "Images", "props": {
        "displayName": "Generates a batch of images from a text caption on a given DALLE model deployment",
        "method": "POST",
        "urlTemplate": "/deployments/{deployment-id}/images/generations?api-version={api-version}",
        "templateParameters": [
            {"name": "deployment-id", "type": "string", "required": True},
            {"name": "api-version", "type": "string", "required": True}],
        "description": "Generates a batch of images from a text caption on a given DALLE model deployment."}},
    {"name": "Create_Response", "tag": "Responses", "props": {
        "displayName": "Creates a model response.",
        "method": "POST",
        "urlTemplate": "/responses?api-version={api-version}",
        "templateParameters": [
            {"name": "api-version", "type": "string", "required": True}],
        "description": "Creates a model response."}},
    {"name": "Get_Response", "tag": "Responses", "props": {
        "displayName": "Retrieves a model response with the given ID.",
        "method": "GET",
        "urlTemplate": "/responses/{response_id}?api-version={api-version}",
        "templateParameters": [
            {"name": "response_id", "type": "string", "required": True},
            {"name": "api-version", "type": "string", "required": True}],
        "description": "Retrieves a model response with the given ID."}},
    {"name": "Get_Input_Items", "tag": "Responses", "props": {
        "displayName": "Returns a list of input items for a given response.",
        "method": "GET",
        "urlTemplate": "/responses/{response_id}/input_items?api-version={api-version}",
        "templateParameters": [
            {"name": "response_id", "type": "string", "required": True},
            {"name": "api-version", "type": "string", "required": True}],
        "description": "Returns a list of input items for a given response."}},
    {"name": "ImageEdit_Create", "tag": "Images", "props": {
        "displayName": "Create Image Edit",
        "method": "POST",
        "urlTemplate": "/v1/images/edits",
        "description": "Edit images via OpenAI-compatible endpoint"}},
    {"name": "ImageGeneration_v1_Create", "tag": "Images", "props": {
        "displayName": "Create Image Generation (v1)",
        "method": "POST",
        "urlTemplate": "/v1/images/generations",
        "description": "Generate images via OpenAI-compatible endpoint"}},
    {"name": "Video_Create", "tag": "Video", "props": {
        "displayName": "Create Video",
        "method": "POST",
        "urlTemplate": "/v1/videos",
        "description": "Submit a video generation task"}},
    {"name": "Video_GetStatus", "tag": "Video", "props": {
        "displayName": "Get Video Status",
        "method": "GET",
        "urlTemplate": "/v1/videos/{videoId}",
        "templateParameters": [
            {"name": "videoId", "type": "string", "required": True, "description": "The video task ID"}],
        "description": "Query the status of a video generation task"}},
    {"name": "Video_GetContent", "tag": "Video", "props": {
        "displayName": "Get Video Content",
        "method": "GET",
        "urlTemplate": "/v1/videos/{videoId}/content",
        "templateParameters": [
            {"name": "videoId", "type": "string", "required": True, "description": "The video task ID"}],
        "description": "Get the generated video content"}},
]

# ── REST helper ──
def az_rest(method, path, body=None):
    url = f'{MGMT}{BASE}{path}?api-version={AV}'
    cmd = ['az', 'rest', '--method', method.lower(), '--url', url]
    if body is not None:
        with open('/tmp/_body.json', 'w', encoding='utf-8') as f:
            json.dump(body, f, ensure_ascii=False)
        cmd += ['--body', '@/tmp/_body.json']
    for attempt in range(3):
        r = subprocess.run(cmd, capture_output=True, text=True)
        if r.returncode == 0:
            return json.loads(r.stdout) if r.stdout.strip() else {}
        err = r.stderr[:300]
        if 'ResourceNotFound' in err or 'NotFound' in err:
            return None
        if attempt < 2:
            print(f'  retry {attempt+1}... {err[:100]}')
            time.sleep(10)
    print(f'FAIL: {method} {path}\n{r.stderr[:500]}', file=sys.stderr)
    sys.exit(1)

# ══════════════════════════════════════
# Step 0: 下载 & 预处理 spec
# ══════════════════════════════════════
print(f'\n[Step 0] Download & preprocess spec')
print(f'  URL: {SPEC_URL}')
spec = json.loads(urlopen(SPEC_URL).read().decode('utf-8'))

HTTP = ('get', 'post', 'put', 'delete', 'patch', 'head', 'options')
orig = sum(1 for p in spec.get('paths', {}).values() for m in HTTP if m in p)
print(f'  Original: {orig} ops, OpenAPI {spec.get("openapi", "")}')

# 3.1 → 3.0.3
if spec.get('openapi', '').startswith('3.1'):
    spec['openapi'] = '3.0.3'
    print('  Downgraded to 3.0.3')

# resolve $ref in description fields
def resolve_ref(obj, root):
    if isinstance(obj, dict):
        for k, v in list(obj.items()):
            if k == 'description' and isinstance(v, dict) and '$ref' in v:
                parts = v['$ref'].lstrip('#/').split('/')
                t = root
                for p in parts:
                    t = t.get(p, {}) if isinstance(t, dict) else t
                if isinstance(t, str):
                    obj[k] = t
            else:
                resolve_ref(v, root)
    elif isinstance(obj, list):
        for i in obj:
            resolve_ref(i, root)

resolve_ref(spec, spec)

# strip untagged operations
removed = 0
for path in list(spec.get('paths', {}).keys()):
    item = spec['paths'][path]
    for m in HTTP:
        if m in item and not item[m].get('tags'):
            del item[m]
            removed += 1
    if not any(m in item for m in HTTP):
        del spec['paths'][path]

final = sum(1 for p in spec['paths'].values() for m in HTTP if m in p)
print(f'  Processed: {final} ops (stripped {removed})')

# ══════════════════════════════════════
# Step 1: Backend
# ══════════════════════════════════════
print(f'\n[Step 1] PUT backend: {BACKEND_ID}')
az_rest('PUT', f'/backends/{BACKEND_ID}', {
    'properties': {
        'url': BACKEND_URL,
        'protocol': 'http',
        'credentials': {
            'managedIdentity': {
                'resource': 'https://cognitiveservices.azure.com/'
            }
        }
    }
})
print('  OK')

# ══════════════════════════════════════
# Step 2: Import API (inline spec)
# ══════════════════════════════════════
print(f'\n[Step 2] PUT API: {API_NAME} (path={API_PATH})')
az_rest('PUT', f'/apis/{API_NAME}', {
    'properties': {
        'format': 'openapi+json',
        'value': json.dumps(spec, ensure_ascii=False),
        'path': API_PATH,
        'displayName': DISPLAY_NAME,
        'protocols': ['https'],
        'subscriptionRequired': True,
        'apiType': 'http'
    }
})

# wait for async provisioning
for i in range(24):
    time.sleep(5)
    try:
        r = az_rest('GET', f'/apis/{API_NAME}')
        if r and r.get('properties', {}).get('displayName'):
            print('  Ready')
            break
    except Exception:
        pass
    print(f'  waiting ({i+1}/24)')

# ══════════════════════════════════════
# Step 3: Policy
# ══════════════════════════════════════
print(f'\n[Step 3] PUT policy → {BACKEND_ID}')
policy_xml = (
    '<policies><inbound><base />'
    f'<set-backend-service id="apim-generated-policy" backend-id="{BACKEND_ID}" />'
    '</inbound><backend><base /></backend>'
    '<outbound><base /></outbound>'
    '<on-error><base /></on-error></policies>'
)
az_rest('PUT', f'/apis/{API_NAME}/policies/policy', {
    'properties': {'format': 'xml', 'value': policy_xml}
})
print('  OK')

# ══════════════════════════════════════
# Step 4: Custom operations + tags
# ══════════════════════════════════════
print('\n[Step 4] Custom operations')

# existing operation url map
existing = {}
result = az_rest('GET', f'/apis/{API_NAME}/operations')
if result and 'value' in result:
    for op in result['value']:
        key = f"{op['properties']['method']} {op['properties']['urlTemplate'].split('?')[0]}"
        existing[key] = op['name']
print(f'  existing: {len(existing)} ops')

created = skipped = 0
for op in CUSTOM_OPS:
    key = f"{op['props']['method']} {op['props']['urlTemplate'].split('?')[0]}"
    if key in existing:
        skipped += 1
        continue
    az_rest('PUT', f"/apis/{API_NAME}/operations/{op['name']}", {'properties': op['props']})
    existing[key] = op['name']
    created += 1
print(f'  created: {created}, skipped: {skipped}')

# tags: create service-level then associate
tags_ensured = set()
tag_ok = 0
for op in CUSTOM_OPS:
    tag = op.get('tag')
    if not tag:
        continue
    if tag not in tags_ensured:
        az_rest('PUT', f'/tags/{tag}', {'properties': {'displayName': tag}})
        tags_ensured.add(tag)
    key = f"{op['props']['method']} {op['props']['urlTemplate'].split('?')[0]}"
    name = existing.get(key, op['name'])
    az_rest('PUT', f"/apis/{API_NAME}/operations/{name}/tags/{tag}", {})
    tag_ok += 1
print(f'  tags: {tag_ok}')

# ══════════════════════════════════════
# Verify & output
# ══════════════════════════════════════
result = az_rest('GET', f'/apis/{API_NAME}/operations')
total = len(result.get('value', [])) if result else 0
print(f'\n[Verify] Total ops: {total}')

out_dir = os.environ.get('AZ_SCRIPTS_OUTPUT_DIRECTORY', '.')
with open(os.path.join(out_dir, 'result.json'), 'w') as f:
    json.dump({'totalOps': total, 'apiName': API_NAME, 'apiPath': API_PATH}, f)

print(f'\nDone: {API_NAME}')
