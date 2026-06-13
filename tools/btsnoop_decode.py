#!/usr/bin/env python3
"""Independent AwoX/Eglo btsnoop decoder (cross-check for the .NET tool).

Parses an Android btsnoop_hci.log, reconstructs each connection's AwoX session key from the login
handshake (pair char 0x001E or 0x001B), and decrypts every status notification on handle 0x0012.
Crypto mirrors AwoxMeshProtocol exactly.

Usage: python btsnoop_decode.py <file> [meshName] [meshPassword]
"""
import sys, struct
from collections import defaultdict
from Crypto.Cipher import AES

MESH_NAME = sys.argv[2] if len(sys.argv) > 2 else "Y4kl7frn"
MESH_PW   = sys.argv[3] if len(sys.argv) > 3 else "0dgq5qfC"

def xor_creds(name, pw):
    n = name.encode()[:16].ljust(16, b"\0")
    p = pw.encode()[:16].ljust(16, b"\0")
    return bytes(a ^ b for a, b in zip(n, p))

def aw_encrypt(key, value):                      # AwoxMeshProtocol.Encrypt
    k = key[:16].ljust(16, b"\0")[::-1]
    v = value[:16].ljust(16, b"\0")[::-1]
    return AES.new(k, AES.MODE_ECB).encrypt(v)[::-1]

def aw_decrypt(key, enc16):                       # AwoxMeshProtocol.DecryptZigbee
    k = bytes(key[:16])[::-1]
    v = bytes(enc16[:16])[::-1]
    return AES.new(k, AES.MODE_ECB).decrypt(v)[::-1]

def session_key(name, pw, sr8, dr8):              # MakeSessionKey
    return aw_encrypt(xor_creds(name, pw), bytes(sr8[:8]) + bytes(dr8[:8]))

data = open(sys.argv[1], "rb").read()
assert data[:7] == b"btsnoop", "not a btsnoop file"

conn_mac, conn_key, conn_sr, read_req = {}, {}, {}, {}
reasm = {}
notif = []   # (conn, flag, handle, val, block)
logins = []  # (conn, kind, handle, hex)
raw_by_conn = defaultdict(list)
all_notif_meta = []  # every 0x1b: (conn, handle, value_len)
att_op_count = defaultdict(int)
read_resps = []  # every 0x0b: (conn, handle, hex)
pos = 16
while pos + 24 <= len(data):
    orig, incl, flags, drops = struct.unpack(">IIII", data[pos:pos+16])
    pos += 24
    pkt = data[pos:pos+incl]; pos += incl
    if not pkt: continue
    h4 = pkt[0]
    if h4 == 0x04:  # HCI event: capture LE Connection Complete -> conn handle + peer MAC
        ev = pkt[1:]
        if len(ev) >= 12 and ev[0] == 0x3e and ev[2] in (0x01, 0x0a):
            handle = ev[4] | (ev[5] << 8)
            addr = ev[8:14]
            conn_mac[handle] = ":".join(f"{addr[5-i]:02x}" for i in range(6))
        continue
    if h4 != 0x02 or len(pkt) < 5: continue
    hf = pkt[1] | (pkt[2] << 8)
    conn = hf & 0x0FFF; pb = (hf >> 12) & 3
    alen = pkt[3] | (pkt[4] << 8)
    acl = pkt[5:5+alen]
    if pb in (0x02, 0x00):
        reasm[conn] = bytearray(acl)
    elif pb == 0x01 and conn in reasm:
        reasm[conn] += acl
    else:
        continue
    fr = reasm[conn]
    if len(fr) < 4: continue
    l2len = fr[0] | (fr[1] << 8); cid = fr[2] | (fr[3] << 8)
    if len(fr) < 4 + l2len: continue
    att = bytes(fr[4:4+l2len]); del reasm[conn]
    if cid != 0x0004 or not att: continue

    op = att[0]
    _h = (att[1] | (att[2] << 8)) if len(att) >= 3 else 0
    att_op_count[(conn, op, _h)] += 1
    if op == 0x0a and len(att) >= 3:
        read_req[conn] = att[1] | (att[2] << 8)
    elif op in (0x12, 0x52) and len(att) >= 3:
        wh = att[1] | (att[2] << 8)
        val = att[3:]
        if len(val) >= 17 and val[0] == 0x0C:
            conn_sr[conn] = val[1:9]
            logins.append((conn, "WRITE", wh, val.hex()))
    elif op == 0x0b:
        val = att[1:]
        rh = read_req.get(conn, 0)
        read_resps.append((conn, rh, val.hex()))   # capture EVERY read response
        if len(val) >= 9 and val[0] == 0x0D and rh in (0x001E, 0x001B) and conn in conn_sr:
            conn_key[conn] = session_key(MESH_NAME, MESH_PW, conn_sr[conn], val[1:9])
            logins.append((conn, "REPLY", rh, val.hex()))
    elif op == 0x1b and len(att) >= 3:
        handle = att[1] | (att[2] << 8)
        val = att[3:]
        all_notif_meta.append((conn, handle, len(val)))
        if len(val) >= 17:
            block = aw_decrypt(conn_key[conn], val[1:17]) if conn in conn_key else None
            notif.append((conn, val[0], handle, val, block))
            raw_by_conn[conn].append(val.hex())

print("=== connections ===")
for c in sorted(set(list(conn_mac) + list(conn_key))):
    print(f"  conn=0x{c:03X} mac={conn_mac.get(c,'?')} key={'yes' if c in conn_key else 'NO'}")

print(f"\n=== {len(notif)} notifications on 0x0012 ===")
# per-connection counts (decoded vs raw)
cnt = defaultdict(lambda: [0, 0])
for conn, flag, h, val, block in notif:
    cnt[conn][0] += 1
    if block is not None: cnt[conn][1] += 1
for conn in sorted(cnt):
    print(f"  conn=0x{conn:03X} frames={cnt[conn][0]} decoded={cnt[conn][1]} key={'yes' if conn in conn_key else 'NO'}")

print("\n=== decoded: distinct blocks per (conn, meshId) ===")
per = defaultdict(set)
for conn, flag, h, val, block in notif:
    if block is None: continue
    mid = block[0] | (block[1] << 8)
    per[(conn, mid)].add(block.hex())
for (conn, mid), blocks in sorted(per.items(), key=lambda kv: -len(kv[1])):
    print(f"  conn=0x{conn:03X} meshId=0x{mid:04X}  distinct={len(blocks)}")

print("\n=== login packets (write 0x0C / reply 0x0D) ===")
for conn, kind, h, hx in logins:
    print(f"  conn=0x{conn:03X} {kind} handle=0x{h:04X} {hx.upper()}")

import os
TARGET = int(os.environ.get("DUMP_CONN", "0x042"), 16)
print(f"\n=== raw frames for conn 0x{TARGET:03X} (first 12) ===")
for v in raw_by_conn.get(TARGET, [])[:12]:
    print(f"  {v.upper()}")

print("\n=== notification handles seen ===")
hc = defaultdict(int)
for conn, flag, h, val, block in notif: hc[h] += 1
for h in sorted(hc): print(f"  handle 0x{h:04X}: {hc[h]}")

print("\n=== ALL distinct decoded blocks (full) ===")
allb = set()
for conn, flag, h, val, block in notif:
    if block is not None: allb.add(block.hex())
for b in sorted(allb):
    mark = "  <-- COLOUR-CMD pattern" if "010003" in b else ""
    print(f"  {b.upper()}{mark}")

TRACE = int(os.environ.get("TRACE_MESH", "0xB627"), 16)
print(f"\n=== time-ordered trace for meshId 0x{TRACE:04X} (consecutive dupes collapsed) ===")
last = None
for conn, flag, h, val, block in notif:
    if block is None: continue
    if (block[0] | (block[1] << 8)) != TRACE: continue
    key_t = block.hex()
    if key_t == last: continue
    last = key_t
    # show flag + full block; highlight the variable tail [12..15]
    print(f"  flag=0x{flag:02X} [2..6]={block[2:6].hex().upper()} mode={block[12]:02X} b13={block[13]:02X} b14={block[14]:02X} b15={block[15]:02X}")

print("\n=== ALL 0x1b notifications by (conn, handle, len) ===")
nm = defaultdict(int)
for c, h, ln in all_notif_meta: nm[(c, h, ln)] += 1
for (c, h, ln), n in sorted(nm.items()): print(f"  conn=0x{c:03X} handle=0x{h:04X} len={ln}: {n}")

print("\n=== complete ATT op inventory (conn, op, handle) ===")
for (c, o, h), n in sorted(att_op_count.items()): print(f"  conn=0x{c:03X} op=0x{o:02X} handle=0x{h:04X}: {n}")

print("\n=== READ RESPONSES: distinct values per (conn, handle) ===")
rr = defaultdict(set)
for c, h, hx in read_resps: rr[(c, h)].add(hx)
for (c, h), vals in sorted(rr.items(), key=lambda kv: -len(kv[1])):
    print(f"  conn=0x{c:03X} handle=0x{h:04X}: {len(vals)} distinct")
    if len(vals) > 1:
        for v in sorted(vals)[:30]:
            print(f"      {v.upper()}")

print("\n=== UNDECODED conns: distinct raw frames ===")
raw = defaultdict(set)
for conn, flag, h, val, block in notif:
    if block is None: raw[conn].add(val.hex())
for conn in sorted(raw):
    print(f"  conn=0x{conn:03X}: {len(raw[conn])} distinct raw (no key) mac={conn_mac.get(conn,'?')}")
