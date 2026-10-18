#!/usr/bin/env python3
"""Run after restarting both backend and PostgreSQL. Reads only ignored test credentials."""
import json
from pathlib import Path
from urllib.request import Request, urlopen
root = Path(__file__).resolve().parent.parent
probe = json.loads((root / '.local/persistence-probe.json').read_text())
body = json.dumps({'loginName': probe['loginName'], 'password': probe['password']}).encode()
request = Request('http://127.0.0.1:5080/api/auth/login', data=body, headers={'Content-Type':'application/json'})
with urlopen(request, timeout=10) as response:
    user = json.load(response)['user']
assert user['id'] == probe['id'] and user['nickname'] == probe['nickname']
print('PASS persisted user ID, password login and updated nickname after database/backend restart')
