"""
Mitsubishi Comment CSV → TIA Portal Tag Table XML 변환기

Usage:
    python convert_comment_to_tagtable.py <comment.csv> [output.xml]

- Input:  UTF-16LE TAB-separated CSV (Device Name | English Comment)
- Output: TIA Portal V20 Openness Tag Table XML (UTF-8)
"""

import csv
import io
import sys
import re
from pathlib import Path
from xml.sax.saxutils import escape


# ── Mitsubishi → Siemens 디바이스 매핑 ──

def convert_device_address(device: str) -> tuple[str, str, str] | None:
    """
    미쯔비시 디바이스를 지멘스 주소로 변환.
    Returns: (logical_address, data_type, device_prefix) or None if unsupported
    """
    device = device.strip()
    if not device:
        return None

    # X: Input bit → %I
    m = re.match(r'^X([0-9A-Fa-f]+)$', device)
    if m:
        hex_val = int(m.group(1), 16)
        byte_addr = hex_val // 16
        bit_addr = hex_val % 16
        if bit_addr > 7:
            # Mitsubishi hex addressing: use raw mapping
            byte_addr = hex_val // 8
            bit_addr = hex_val % 8
        return (f'%I{byte_addr}.{bit_addr}', 'Bool', 'X')

    # Y: Output bit → %Q
    m = re.match(r'^Y([0-9A-Fa-f]+)$', device)
    if m:
        hex_val = int(m.group(1), 16)
        byte_addr = hex_val // 16
        bit_addr = hex_val % 16
        if bit_addr > 7:
            byte_addr = hex_val // 8
            bit_addr = hex_val % 8
        return (f'%Q{byte_addr}.{bit_addr}', 'Bool', 'Y')

    # M: Internal bit → %M
    m = re.match(r'^M(\d+)$', device)
    if m:
        num = int(m.group(1))
        byte_addr = num // 8
        bit_addr = num % 8
        return (f'%M{byte_addr}.{bit_addr}', 'Bool', 'M')

    # SM: Special relay → %M (high area)
    m = re.match(r'^SM(\d+)$', device)
    if m:
        num = int(m.group(1))
        byte_addr = 10000 + num // 8
        bit_addr = num % 8
        return (f'%M{byte_addr}.{bit_addr}', 'Bool', 'SM')

    # D: Data register (16-bit) → %MW
    m = re.match(r'^D(\d+)$', device)
    if m:
        num = int(m.group(1))
        return (f'%MW{num * 2}', 'Int', 'D')

    # SD: Special register → %MW (high area)
    m = re.match(r'^SD(\d+)$', device)
    if m:
        num = int(m.group(1))
        return (f'%MW{20000 + num * 2}', 'Int', 'SD')

    # R: Float register → %MD
    m = re.match(r'^R(\d+)$', device)
    if m:
        num = int(m.group(1))
        return (f'%MD{num * 4}', 'Real', 'R')

    # T: Timer → %MW (timer area)
    m = re.match(r'^T(\d+)$', device)
    if m:
        num = int(m.group(1))
        return (f'%MW{30000 + num * 2}', 'Int', 'T')

    # L: Latch relay → %M (latch area)
    m = re.match(r'^L(\d+)$', device)
    if m:
        num = int(m.group(1))
        byte_addr = 5000 + num // 8
        bit_addr = num % 8
        return (f'%M{byte_addr}.{bit_addr}', 'Bool', 'L')

    # ZR: File register → %MW
    m = re.match(r'^ZR(\d+)$', device)
    if m:
        num = int(m.group(1))
        return (f'%MW{40000 + num * 2}', 'Int', 'ZR')

    # U\G: Buffer memory (skip - module specific)
    if device.startswith('U'):
        return None

    return None


# ── XML 생성 ──

XML_HEADER = '''<?xml version="1.0" encoding="utf-8"?>
<Document>
  <Engineering version="V20" />
  <DocumentInfo>
    <Created>2026-04-08T00:00:00Z</Created>
    <ExportSetting>WithDefaults</ExportSetting>
    <InstalledProducts>
      <Product>
        <DisplayName>Totally Integrated Automation Portal</DisplayName>
        <DisplayVersion>V20 Update 4</DisplayVersion>
      </Product>
      <Product>
        <DisplayName>STEP 7 Professional</DisplayName>
        <DisplayVersion>V20 Update 4</DisplayVersion>
      </Product>
    </InstalledProducts>
  </DocumentInfo>
  <SW.Tags.PlcTagTable ID="0">
    <AttributeList>
      <Name>{table_name}</Name>
    </AttributeList>
    <ObjectList>
'''

XML_TAG = '''      <SW.Tags.PlcTag ID="{id}" CompositionName="Tags">
        <AttributeList>
          <DataTypeName>{data_type}</DataTypeName>
          <ExternalAccessible>true</ExternalAccessible>
          <ExternalVisible>true</ExternalVisible>
          <ExternalWritable>true</ExternalWritable>
          <LogicalAddress>{address}</LogicalAddress>
          <Name>{name}</Name>
        </AttributeList>
        <ObjectList>
          <MultilingualText ID="{comment_id}" CompositionName="Comment">
            <ObjectList>
              <MultilingualTextItem ID="{comment_item_id}" CompositionName="Items">
                <AttributeList>
                  <Culture>en-US</Culture>
                  <Text>{comment}</Text>
                </AttributeList>
              </MultilingualTextItem>
            </ObjectList>
          </MultilingualText>
        </ObjectList>
      </SW.Tags.PlcTag>
'''

XML_FOOTER = '''    </ObjectList>
  </SW.Tags.PlcTagTable>
</Document>
'''


def read_comment_csv(filepath: str) -> list[tuple[str, str]]:
    """Read Mitsubishi comment CSV (UTF-16LE, TAB-separated)."""
    raw = Path(filepath).read_bytes()
    text = raw.decode('utf-16-le')
    if text.startswith('\ufeff'):
        text = text[1:]

    reader = csv.reader(io.StringIO(text), delimiter='\t')
    next(reader)  # skip header line 1 (project name)
    next(reader)  # skip header line 2 (column names)

    rows = []
    for row in reader:
        if not row or not row[0].strip():
            continue
        device = row[0].strip()
        comment = row[1].strip() if len(row) > 1 else ''
        rows.append((device, comment))

    return rows


def generate_tag_table_xml(rows: list[tuple[str, str]], table_name: str) -> str:
    """Convert device/comment pairs to TIA Tag Table XML."""
    xml = XML_HEADER.format(table_name=escape(table_name))

    tag_id = 1
    seen_addresses = set()
    skipped = {'count': 0, 'types': set()}

    for device, comment in rows:
        result = convert_device_address(device)
        if result is None:
            skipped['count'] += 1
            prefix = ''
            for c in device:
                if c.isalpha():
                    prefix += c
                else:
                    break
            skipped['types'].add(prefix)
            continue

        address, data_type, dev_prefix = result

        # Skip duplicate addresses
        if address in seen_addresses:
            continue
        seen_addresses.add(address)

        # Use device name as tag name (sanitize for TIA)
        tag_name = device

        xml += XML_TAG.format(
            id=tag_id,
            data_type=data_type,
            address=address,
            name=escape(tag_name),
            comment=escape(comment),
            comment_id=tag_id + 1,
            comment_item_id=tag_id + 2,
        )
        tag_id += 3  # Each tag uses 3 IDs

    xml += XML_FOOTER

    print(f'Generated {len(seen_addresses)} tags')
    if skipped["count"] > 0:
        print(f'Skipped {skipped["count"]} unsupported devices: {", ".join(sorted(skipped["types"]))}')

    return xml


def main():
    if len(sys.argv) < 2:
        print('Usage: python convert_comment_to_tagtable.py <comment.csv> [output.xml]')
        sys.exit(1)

    input_file = sys.argv[1]
    output_file = sys.argv[2] if len(sys.argv) > 2 else input_file.rsplit('.', 1)[0] + '_tags.xml'

    rows = read_comment_csv(input_file)
    print(f'Read {len(rows)} comment rows from {input_file}')

    table_name = Path(input_file).stem.replace('_Comment', '')
    xml = generate_tag_table_xml(rows, table_name)

    Path(output_file).write_text(xml, encoding='utf-8')
    print(f'Saved to {output_file}')


if __name__ == '__main__':
    main()
