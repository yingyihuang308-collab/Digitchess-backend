#!/usr/bin/env python3
"""Concurrent refresh against the local disposable test account; never prints tokens."""
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from urllib.error import HTTPError
from urllib.request import Request, urlopen
import json
root = Path(__file__).resolve().parent.parent
probe = json.loads((root / '.local/persistence-probe.json').read_text())
def post(path, data):
    request = Request('http://127.0.0.1:5080' + path, data=json.dumps(data).encode(), headers={'Content-Type':'application/json'})
    try:
        with urlopen(request, timeout=15) as response:
            return response.status, json.load(response)
    except HTTPError as error:
        return error.code, json.load(error)
status, login = post('/api/auth/login', {'loginName':probe['loginName'], 'password':probe['password']})
assert status == 200
print('PASS independent login creates a refresh family')
with ThreadPoolExecutor(max_workers=2) as pool:
    results = list(pool.map(lambda _: post('/api/auth/refresh', {'refreshToken':login['refreshToken']}), range(2)))
assert sorted(status for status, _ in results) == [200, 401]
print('PASS concurrent refresh produces one success and one reuse rejection')
next_token = next(data['refreshToken'] for status, data in results if status == 200)
assert post('/api/auth/refresh', {'refreshToken':next_token})[0] == 401
print('PASS concurrent reuse revokes the newly issued descendant')
