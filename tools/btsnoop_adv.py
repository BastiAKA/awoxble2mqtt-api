#!/usr/bin/env python3
"""Extract BLE *advertisements* (not GATT) from an Android btsnoop_hci.log.

Hypothesis: the AwoX/Telink lamp broadcasts its live state (incl. colour/hue) as a
non-connectable advertisement. A scanning phone shows the correct colour WITHOUT ever
connecting/logging in — so the colour lives in the advert payload, not in ATT reads or
notifications. The existing decoder ignores advertising reports entirely; this one parses
ONLY them.

For the given target MACs it prints, per advertiser:
  - every distinct manufacturer-specific data blob over time
  - a column-diff that marks which byte offsets VARY (those track the colour)

Usage: python btsnoop_adv.py <file> [mac1 mac2 ...]
"""
import sys, struct
from collections import defaultdict, OrderedDict

TARGETS = {m.lower().replace("-", ":") for m in sys.argv[2:]} or {
    "aa:bb:cc:dd:ee:ff", "aa:bb:cc:dd:ee:00",   # example MACs — pass your own as args
}

data = open(sys.argv[1], "rb").read()
assert data[:7] == b"btsnoop", "not a btsnoop file"


def mac_str(addr6_le):  # advert addresses are little-endian
    return ":".join(f"{addr6_le[5 - i]:02x}" for i in range(6))


def parse_ad_structures(ad):
    """Yield (type, value_bytes) for each AD structure in an advert payload."""
    i = 0
    while i < len(ad):
        ln = ad[i]
        if ln == 0 or i + 1 + ln > len(ad):
            break
        yield ad[i + 1], ad[i + 2:i + 1 + ln]
        i += 1 + ln


# per-target: ordered list of (ts_index, full_adv_bytes, manuf_value)
records = defaultdict(list)
seq = 0
pos = 16
while pos + 24 <= len(data):
    orig, incl, flags, drops = struct.unpack(">IIII", data[pos:pos + 16])
    pos += 24
    pkt = data[pos:pos + incl]
    pos += incl
    if len(pkt) < 4 or pkt[0] != 0x04 or pkt[1] != 0x3e:
        continue
    sub = pkt[3]
    reports = []
    if sub == 0x02:  # legacy LE Advertising Report
        n = pkt[4]
        p = 5
        # layout: n*(evt_type), n*(addr_type), n*(addr6), n*(data_len), n*(data), n*(rssi)
        # Android logs them per-report sequentially instead; handle the common per-report form:
        for _ in range(n):
            if p + 9 > len(pkt):
                break
            evt_type = pkt[p]; addr_type = pkt[p + 1]
            addr = pkt[p + 2:p + 8]
            dlen = pkt[p + 8]
            ad = pkt[p + 9:p + 9 + dlen]
            p += 9 + dlen + 1  # +1 rssi
            reports.append((addr, ad))
    elif sub == 0x0d:  # LE Extended Advertising Report
        n = pkt[4]
        p = 5
        for _ in range(n):
            if p + 24 > len(pkt):
                break
            # evt_type(2) addr_type(1) addr(6) prim_phy(1) sec_phy(1) sid(1) tx(1)
            # rssi(1) per_int(2) dir_addr_type(1) dir_addr(6) data_len(1) data(...)
            addr = pkt[p + 3:p + 9]
            dlen = pkt[p + 23]
            ad = pkt[p + 24:p + 24 + dlen]
            p += 24 + dlen
            reports.append((addr, ad))
    else:
        continue

    for addr, ad in reports:
        m = mac_str(addr)
        if TARGETS and m not in TARGETS:
            continue
        manuf = None
        for t, v in parse_ad_structures(ad):
            if t == 0xFF:  # Manufacturer Specific Data
                manuf = v
        records[m].append((seq, bytes(ad), manuf))
        seq += 1

if not records:
    print("No advertisements found for targets:", ", ".join(sorted(TARGETS)))
    print("(Maybe the capture used extended adverts 0x0d, or the phone never scanned.)")
    sys.exit(0)

for mac in sorted(records):
    rows = records[mac]
    print(f"\n========== {mac}  ({len(rows)} adverts) ==========")

    # distinct full advert payloads (in first-seen order)
    distinct_full = OrderedDict()
    for _, full, _ in rows:
        distinct_full.setdefault(full.hex(), 0)
        distinct_full[full.hex()] += 1
    print(f"  distinct full adverts: {len(distinct_full)}")

    # focus on manufacturer data
    manuf_rows = [(s, mv) for s, _, mv in rows if mv is not None]
    if not manuf_rows:
        print("  no manufacturer-specific (0xFF) data; dumping distinct full adverts:")
        for hx, c in list(distinct_full.items())[:40]:
            print(f"    x{c:<4} {hx.upper()}")
        continue

    distinct_manuf = OrderedDict()
    for _, mv in manuf_rows:
        distinct_manuf.setdefault(mv.hex(), 0)
        distinct_manuf[mv.hex()] += 1
    print(f"  manufacturer(0xFF) frames: {len(manuf_rows)}  distinct: {len(distinct_manuf)}")
    company = manuf_rows[0][1][:2]
    print(f"  company id (LE): {company.hex().upper()}  len: {len(manuf_rows[0][1])}")

    # column diff: which byte offsets vary across distinct manuf blobs?
    blobs = [bytes.fromhex(h) for h in distinct_manuf]
    maxlen = max(len(b) for b in blobs)
    varying = []
    for off in range(maxlen):
        vals = {b[off] for b in blobs if off < len(b)}
        if len(vals) > 1:
            varying.append(off)
    print(f"  varying byte offsets: {varying}")

    print("  distinct manufacturer blobs (most frequent first):")
    for hx, c in sorted(distinct_manuf.items(), key=lambda kv: -kv[1])[:40]:
        b = bytes.fromhex(hx)
        # annotate the varying offsets inline
        marks = "".join(
            f"[{off}:{b[off]:02X}]" for off in varying if off < len(b)
        )
        print(f"    x{c:<5} {hx.upper()}   vary {marks}")
