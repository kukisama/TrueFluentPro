#!/usr/bin/env python3
"""
预处理 Azure OpenAI spec: 3.1→3.0.3, resolve $ref descriptions, strip untagged ops.
生成可直接被 ARM 模板导入的 openai-spec-processed.json。
"""
import json, sys
from urllib.request import urlopen

SPEC_URL = "https://raw.githubusercontent.com/Azure/azure-rest-api-specs/main/specification/cognitiveservices/data-plane/AzureOpenAI/inference/preview/2025-03-01-preview/inference.json"
OUTPUT = "openai-spec-processed.json"
HTTP = ('get', 'post', 'put', 'delete', 'patch', 'head', 'options')

print(f'Downloading spec from {SPEC_URL}...')
spec = json.loads(urlopen(SPEC_URL).read().decode('utf-8'))
orig = sum(1 for p in spec.get('paths', {}).values() for m in HTTP if m in p)
print(f'Original: {orig} ops, OpenAPI {spec.get("openapi", "")}')

# 3.1 → 3.0.3
if spec.get('openapi', '').startswith('3.1'):
    spec['openapi'] = '3.0.3'
    print('Downgraded to 3.0.3')

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
print(f'Processed: {final} ops (stripped {removed} untagged)')

with open(OUTPUT, 'w', encoding='utf-8') as f:
    json.dump(spec, f, ensure_ascii=False, indent=2)

size_kb = len(json.dumps(spec, ensure_ascii=False)) // 1024
print(f'Saved to {OUTPUT} ({size_kb} KB)')
