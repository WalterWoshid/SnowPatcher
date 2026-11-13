"""Publish a release ZIP and verify the approved download. Credentials stay in the environment."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

BASE = 'https://g-306.modapi.io/v1'


def request(method, path, data=None, content_type=None):
    token = os.environ.get('MODIO_TOKEN')
    if not token:
        raise RuntimeError('MODIO_TOKEN is not configured')
    headers = {'Authorization': 'Bearer ' + token, 'User-Agent': 'SnowPatcher-Releases'}
    if content_type:
        headers['Content-Type'] = content_type
    req = urllib.request.Request(BASE + path, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=180) as response:
            return json.load(response)
    except urllib.error.HTTPError as error:
        raise RuntimeError(f'mod.io returned HTTP {error.code} for {path.split("?")[0]}') from None


def files(mod_id):
    result = []
    while True:
        page = request('GET', f'/games/306/mods/{mod_id}/files?_limit=100&_offset={len(result)}')
        rows = page.get('data', [])
        result.extend(rows)
        if not rows or len(result) >= page.get('result_total', len(result)):
            return result


def trusted_host(host):
    return host in ('g-306.modapi.io', 'api.mod.io') or host.endswith(('.modcdn.io', '.mod.io'))


def windows_file_id(listing):
    windows = next((p for p in listing.get('platforms', []) if p['platform'] == 'windows'), None)
    return windows.get('modfile_live') if windows else (listing.get('modfile') or {}).get('id')


class DownloadRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, url):
        parsed = urllib.parse.urlsplit(url)
        if parsed.scheme != 'https' or not trusted_host(parsed.hostname or ''):
            raise RuntimeError('Download redirected outside mod.io')
        redirected = super().redirect_request(req, fp, code, msg, headers, url)
        if redirected:
            redirected.remove_header('Authorization')
        return redirected


def verify_download(item, expected_sha256):
    url = (item.get('download') or {}).get('binary_url', '')
    parsed = urllib.parse.urlsplit(url)
    if parsed.scheme != 'https' or not trusted_host(parsed.hostname or ''):
        raise RuntimeError('mod.io did not provide a trusted download URL')
    headers = {'User-Agent': 'SnowPatcher-Releases'}
    if parsed.hostname in ('g-306.modapi.io', 'api.mod.io'):
        headers['Authorization'] = 'Bearer ' + os.environ['MODIO_TOKEN']
    digest = hashlib.sha256()
    opener = urllib.request.build_opener(DownloadRedirect())
    with opener.open(urllib.request.Request(url, headers=headers), timeout=180) as response:
        for chunk in iter(lambda: response.read(1024 * 1024), b''):
            digest.update(chunk)
    if digest.hexdigest() != expected_sha256:
        raise RuntimeError('The published download failed its SHA-256 check')


def publish(mod_id, archive, version, metadata, changelog):
    body = Path(archive).read_bytes()
    sha256 = hashlib.sha256(body).hexdigest()
    md5 = hashlib.md5(body, usedforsecurity=False).hexdigest()
    metadata = dict(metadata, sha256=sha256)
    listing = request('GET', f'/games/306/mods/{mod_id}')
    existing = [f for f in files(mod_id) if f.get('version') == version]
    if existing:
        item = next((f for f in existing if (f.get('filehash') or {}).get('md5') == md5), None)
        if item is None:
            raise RuntimeError('That version already contains different bytes. Use a new version.')
        if windows_file_id(listing) != item['id']:
            raise RuntimeError('That release exists but is not live. Review it before reactivating it.')
    else:
        boundary = 'SnowPatcher' + uuid.uuid4().hex
        fields = {'version': version, 'changelog': changelog, 'active': 'true',
                  'platforms[]': 'windows', 'filehash': md5, 'metadata_blob': json.dumps(metadata)}
        payload = bytearray()
        for name, value in fields.items():
            payload.extend(f'--{boundary}\r\nContent-Disposition: form-data; name="{name}"\r\n\r\n{value}\r\n'.encode())
        name = Path(archive).name
        if any(c in name for c in '\r\n"'):
            raise ValueError('Invalid release filename')
        payload.extend(f'--{boundary}\r\nContent-Disposition: form-data; name="filedata"; filename="{name}"\r\nContent-Type: application/zip\r\n\r\n'.encode())
        payload.extend(body)
        payload.extend(f'\r\n--{boundary}--\r\n'.encode())
        # A failed POST is reconciled by hash on the next run, never retried blindly.
        item = request('POST', f'/games/306/mods/{mod_id}/files', bytes(payload), 'multipart/form-data; boundary=' + boundary)
    deadline = time.monotonic() + 300
    while True:
        item = request('GET', f'/games/306/mods/{mod_id}/files/{item["id"]}')
        verdict = item.get('virus_positive', 0)
        # mod.io uses 2 for potentially harmful files such as EXE archives.
        # Desktop releases retain that warning; detected threats always stop publication.
        executable_warning = verdict == 2 and metadata.get('format') == 'snowpatcher-application'
        if verdict and not executable_warning:
            raise RuntimeError('mod.io flagged the uploaded file. Review its scan result.')
        windows = next((p for p in item.get('platforms', []) if p['platform'] == 'windows'), None)
        if windows and windows['status'] in (2, 3):
            raise RuntimeError('The Windows file was not approved')
        if windows and windows['status'] == 1:
            break
        if time.monotonic() >= deadline:
            raise RuntimeError('The upload is still awaiting mod.io approval. Rerun to verify it.')
        time.sleep(10)
    live = request('GET', f'/games/306/mods/{mod_id}')
    if windows_file_id(live) != item['id']:
        raise RuntimeError('The verified file is not the live listing download')
    verify_download(item, sha256)
    return {'mod_id': mod_id, 'file_id': item['id'], 'version': version, 'sha256': sha256,
            'url': live['profile_url'], 'approved': True,
            'virus_status': item.get('virus_status'), 'virus_positive': item.get('virus_positive'),
            'executable_warning': executable_warning}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--mod-id', required=True, type=int)
    parser.add_argument('--archive', required=True, type=Path)
    parser.add_argument('--version', required=True)
    parser.add_argument('--metadata', required=True, type=Path)
    parser.add_argument('--changelog', required=True, type=Path)
    args = parser.parse_args()
    result = publish(args.mod_id, args.archive, args.version,
                     json.loads(args.metadata.read_text(encoding='utf-8')),
                     args.changelog.read_text(encoding='utf-8'))
    print(json.dumps(result))


if __name__ == '__main__':
    main()
