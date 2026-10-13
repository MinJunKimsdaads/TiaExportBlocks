"""
Mitsubishi Ladder CSV → TIA Portal OB (LAD) XML 변환기

Usage:
    python convert_ladder_to_ob.py <ladder.csv> [output.xml]

- Input:  UTF-16LE TAB-separated Mitsubishi GX Works ladder export CSV
- Output: TIA Portal V20 Openness OB XML (LAD) (UTF-8)

지원 명령어:
  접점: LD, LDI, LDP, LDF, AND, ANI, OR, ORI, ORP, ORF, MEP, MEF
  비교: LD=, LD<>, LD>=, LD>, AND=, AND<>, AND>=, AND>, AND<=, AND<,
        OR=, ORD>, LDD=, LDD<, LDD>, ANDD<, ANDD>, ANDD>=, ANDD<=
  출력: OUT, SET, RST
  이동: MOV, MOVP, DMOV, DMOVP, FMOVP, BMOVP
  산술: +, +P, D+, D-, D*
  스택: MPS, MRD, MPP
  논리: ANB, ORB, INV
  제어: FOR, NEXT, END
  비트: BKRST
"""

import csv
import io
import sys
import re
from pathlib import Path
from xml.sax.saxutils import escape


# ── CSV Parser ──

def read_ladder_csv(filepath: str) -> list[list[str]]:
    """Read Mitsubishi ladder CSV."""
    raw = Path(filepath).read_bytes()
    text = raw.decode('utf-16-le')
    if text.startswith('\ufeff'):
        text = text[1:]

    reader = csv.reader(io.StringIO(text), delimiter='\t')
    rows = []
    for row in reader:
        rows.append([c.strip() for c in row])
    return rows


def parse_networks(rows: list[list[str]]) -> list[dict]:
    """
    Parse CSV rows into networks.
    Each network = { title: str, comment: str, instructions: [(instr, operands)] }
    """
    networks = []
    current_title = ''
    current_comment = ''
    current_instrs = []
    header_rows = 3  # skip first 3 rows (file header, module info, column header)

    for i, row in enumerate(rows):
        if i < header_rows:
            continue

        # Column mapping: [StepNo, LineStatement, Instruction, Device, Blank, PIStatement, Note]
        line_stmt = row[1] if len(row) > 1 else ''
        instr = row[2] if len(row) > 2 else ''
        device = row[3] if len(row) > 3 else ''

        # Title detection
        if line_stmt.startswith('[Title]'):
            # Save current network if it has instructions
            if current_instrs:
                networks.append({
                    'title': current_title,
                    'comment': current_comment,
                    'instructions': current_instrs,
                })
                current_instrs = []
            current_title = line_stmt.replace('[Title]', '').strip()
            current_comment = ''
            continue

        # Comment/description line (starts with \r\n or *)
        if line_stmt and not instr:
            # New network separator
            desc = line_stmt.replace('\\r\\n', '\n').strip()
            if desc.startswith('*'):
                desc = desc[1:].strip()
            if current_instrs:
                networks.append({
                    'title': current_title,
                    'comment': current_comment,
                    'instructions': current_instrs,
                })
                current_instrs = []
                current_comment = desc
            else:
                current_comment = desc
            continue

        # Instruction line
        if instr:
            if instr == 'END':
                continue
            operands = []
            if device:
                operands.append(device)
            current_instrs.append((instr, operands))
            continue

        # Continuation line (operand on next line for MOVP etc.)
        if device and not instr and current_instrs:
            current_instrs[-1][1].append(device)

    # Last network
    if current_instrs:
        networks.append({
            'title': current_title,
            'comment': current_comment,
            'instructions': current_instrs,
        })

    return networks


# ── Preprocessing ──

def preprocess_inv(instrs: list[tuple[str, list[str]]]) -> list[tuple[str, list[str]]]:
    """
    Apply De Morgan's law to LDI/ANI...INV patterns.
    NOT(NOT A AND NOT B AND NOT C) = A OR B OR C
    """
    result = list(instrs)
    i = 0
    while i < len(result):
        if result[i][0] != 'INV':
            i += 1
            continue

        # Find the preceding block of LDI/ANI (or LD/AND) instructions
        j = i - 1
        while j >= 0 and result[j][0] in ('ANI', 'AND'):
            j -= 1

        if j >= 0 and result[j][0] in ('LDI', 'LD'):
            # Apply De Morgan's law: flip each instruction
            for k in range(j, i):
                bi, bops = result[k]
                if bi == 'LDI':
                    result[k] = ('LD', bops)
                elif bi == 'LD':
                    result[k] = ('LDI', bops)
                elif bi == 'ANI':
                    result[k] = ('OR', bops)
                elif bi == 'AND':
                    result[k] = ('ORI', bops)
            # Remove the INV
            result.pop(i)
            continue
        else:
            # Unknown pattern, remove INV as no-op
            result.pop(i)
            continue

    return result


# ── LAD XML Builder ──

class LadNetworkBuilder:
    """Builds FlgNet XML (Parts + Wires) from parsed instructions."""

    def __init__(self):
        self.uid = 21
        self.parts_xml = []
        self.wires_xml = []

        # current_rail: None (powerrail) | (uid, pin) | [(uid, pin), ...] (OR-merged)
        self.current_rail = None
        self.branch_stack = []   # for MPS/MRD/MPP

        # Deferred powerrail connections → single <Powerrail/> Wire in build_xml
        self._pr_dests = []      # list of (uid, pin) or None (removed by ANB)

        # Track current block's powerrail start index (for ANB redirect)
        self._block_pr_start = 0

        # Logic stack for LD/ORB/ANB: [(saved_rail, saved_block_pr_start), ...]
        self._logic_stack = []

        # Pending coil connections: group coils sharing the same source
        self._pending_coil_source = None
        self._pending_coil_dests = []

    def next_uid(self) -> int:
        uid = self.uid
        self.uid += 1
        return uid

    # ── Rail helpers ──

    @staticmethod
    def _merge(rail1, rail2):
        """Merge two rails for OR. Returns single tuple or list."""
        result = []
        if isinstance(rail1, list):
            result.extend(rail1)
        elif rail1 is not None:
            result.append(rail1)
        if isinstance(rail2, list):
            result.extend(rail2)
        elif rail2 is not None:
            result.append(rail2)
        if len(result) == 0:
            return None
        if len(result) == 1:
            return result[0]
        return result

    def _wire_to_dest(self, dest_uid: int, dest_pin: str) -> None:
        """Create wire from current_rail to destination pin."""
        if self.current_rail is None:
            self._pr_dests.append((dest_uid, dest_pin))
        elif isinstance(self.current_rail, list):
            w = self.next_uid()
            lines = [f'    <Wire UId="{w}">']
            for s_uid, s_pin in self.current_rail:
                lines.append(f'      <NameCon UId="{s_uid}" Name="{s_pin}" />')
            lines.append(f'      <NameCon UId="{dest_uid}" Name="{dest_pin}" />')
            lines.append('    </Wire>')
            self.wires_xml.append('\n'.join(lines))
        else:
            s_uid, s_pin = self.current_rail
            w = self.next_uid()
            self.wires_xml.append(
                f'    <Wire UId="{w}">\n'
                f'      <NameCon UId="{s_uid}" Name="{s_pin}" />\n'
                f'      <NameCon UId="{dest_uid}" Name="{dest_pin}" />\n'
                f'    </Wire>'
            )

    def _wire_rail_to_dests(self, rail, dests: list[tuple[int, str]]) -> None:
        """Create a single wire from a rail (possibly merged) to multiple destinations."""
        if not dests:
            return
        w = self.next_uid()
        lines = [f'    <Wire UId="{w}">']
        if isinstance(rail, list):
            for s_uid, s_pin in rail:
                lines.append(f'      <NameCon UId="{s_uid}" Name="{s_pin}" />')
        elif rail is not None:
            s_uid, s_pin = rail
            lines.append(f'      <NameCon UId="{s_uid}" Name="{s_pin}" />')
        for d_uid, d_pin in dests:
            lines.append(f'      <NameCon UId="{d_uid}" Name="{d_pin}" />')
        lines.append('    </Wire>')
        self.wires_xml.append('\n'.join(lines))

    # ── Logic stack operations ──

    def push_logic(self) -> None:
        """Push current state for LD inside existing network (ORB/ANB prep)."""
        self._logic_stack.append((self.current_rail, self._block_pr_start))
        self._block_pr_start = len(self._pr_dests)
        self.current_rail = None

    def orb_merge(self) -> None:
        """ORB: merge two parallel branches (both stay on powerrail)."""
        if not self._logic_stack:
            return
        saved_rail, saved_pr_start = self._logic_stack.pop()
        self.current_rail = self._merge(saved_rail, self.current_rail)
        self._block_pr_start = saved_pr_start

    def anb_merge(self) -> None:
        """ANB: series merge - redirect current block's powerrail connections to saved_rail."""
        if not self._logic_stack:
            return
        saved_rail, saved_pr_start = self._logic_stack.pop()

        # Collect and remove current block's powerrail connections
        redirected = []
        for i in range(self._block_pr_start, len(self._pr_dests)):
            if self._pr_dests[i] is not None:
                redirected.append(self._pr_dests[i])
                self._pr_dests[i] = None

        # Wire saved_rail → all redirected destinations
        if redirected and saved_rail is not None:
            self._wire_rail_to_dests(saved_rail, redirected)

        self._block_pr_start = saved_pr_start
        # current_rail stays unchanged (output of current block)

    # ── Part builders ──

    def add_access(self, device: str) -> int:
        """Add a variable/constant access and return its UId."""
        uid = self.next_uid()

        # K constant
        if device.startswith('K'):
            val = device[1:]
            self.parts_xml.append(
                f'    <Access Scope="LiteralConstant" UId="{uid}">\n'
                f'      <Constant>\n'
                f'        <ConstantType>Int</ConstantType>\n'
                f'        <ConstantValue>{escape(val)}</ConstantValue>\n'
                f'      </Constant>\n'
                f'    </Access>'
            )
        elif device.startswith('H'):
            val = device[1:]
            self.parts_xml.append(
                f'    <Access Scope="LiteralConstant" UId="{uid}">\n'
                f'      <Constant>\n'
                f'        <ConstantType>Int</ConstantType>\n'
                f'        <ConstantValue>16#{escape(val)}</ConstantValue>\n'
                f'      </Constant>\n'
                f'    </Access>'
            )
        else:
            self.parts_xml.append(
                f'    <Access Scope="GlobalVariable" UId="{uid}">\n'
                f'      <Symbol>\n'
                f'        <Component Name="{escape(device)}" />\n'
                f'      </Symbol>\n'
                f'    </Access>'
            )
        return uid

    def add_contact(self, device: str, negated: bool = False) -> None:
        """Add a contact (LD/AND series)."""
        self.flush_pending_coils()
        dev_uid = self.add_access(device)

        part_uid = self.next_uid()
        if negated:
            self.parts_xml.append(
                f'    <Part Name="Contact" UId="{part_uid}">\n'
                f'      <Negated Name="operand" />\n'
                f'    </Part>'
            )
        else:
            self.parts_xml.append(
                f'    <Part Name="Contact" UId="{part_uid}" />'
            )

        # Wire: current_rail → contact.in (deferred if powerrail)
        self._wire_to_dest(part_uid, 'in')

        # Wire: device → contact.operand
        w = self.next_uid()
        self.wires_xml.append(
            f'    <Wire UId="{w}">\n'
            f'      <IdentCon UId="{dev_uid}" />\n'
            f'      <NameCon UId="{part_uid}" Name="operand" />\n'
            f'    </Wire>'
        )

        self.current_rail = (part_uid, 'out')

    def add_comparison(self, op_name: str, operands: list[str]) -> None:
        """Add a comparison contact (LD=, AND>=, etc.)."""
        self.flush_pending_coils()
        if len(operands) < 2:
            return

        op1_uid = self.add_access(operands[0])
        op2_uid = self.add_access(operands[1])

        part_uid = self.next_uid()
        self.parts_xml.append(
            f'    <Part Name="{op_name}" UId="{part_uid}">\n'
            f'      <TemplateValue Name="Card" Type="Cardinality">2</TemplateValue>\n'
            f'    </Part>'
        )

        # Wire: current_rail → compare.in
        self._wire_to_dest(part_uid, 'in')

        # Wire: operand1 → compare.in1
        w = self.next_uid()
        self.wires_xml.append(
            f'    <Wire UId="{w}">\n'
            f'      <IdentCon UId="{op1_uid}" />\n'
            f'      <NameCon UId="{part_uid}" Name="in1" />\n'
            f'    </Wire>'
        )
        # Wire: operand2 → compare.in2
        w = self.next_uid()
        self.wires_xml.append(
            f'    <Wire UId="{w}">\n'
            f'      <IdentCon UId="{op2_uid}" />\n'
            f'      <NameCon UId="{part_uid}" Name="in2" />\n'
            f'    </Wire>'
        )

        self.current_rail = (part_uid, 'out')

    def flush_pending_coils(self) -> None:
        """Flush buffered coil connections into a single multi-destination wire."""
        if not self._pending_coil_dests:
            return
        src = self._pending_coil_source
        if src is None:
            # Deferred powerrail → coils
            for d_uid, d_pin in self._pending_coil_dests:
                self._pr_dests.append((d_uid, d_pin))
        else:
            self._wire_rail_to_dests(src, self._pending_coil_dests)
        self._pending_coil_source = None
        self._pending_coil_dests = []

    def add_coil(self, device: str, coil_type: str = 'Coil') -> None:
        """Add output coil (OUT/SET/RST)."""
        dev_uid = self.add_access(device)
        part_uid = self.next_uid()
        self.parts_xml.append(
            f'    <Part Name="{coil_type}" UId="{part_uid}" />'
        )

        # Buffer coils sharing the same source into one wire
        current_src = self.current_rail
        if self._pending_coil_dests and self._pending_coil_source != current_src:
            self.flush_pending_coils()

        self._pending_coil_source = current_src
        self._pending_coil_dests.append((part_uid, 'in'))

        # Operand wire (always individual)
        w = self.next_uid()
        self.wires_xml.append(
            f'    <Wire UId="{w}">\n'
            f'      <IdentCon UId="{dev_uid}" />\n'
            f'      <NameCon UId="{part_uid}" Name="operand" />\n'
            f'    </Wire>'
        )

    def add_move(self, src_device: str, dst_device: str) -> None:
        """Add MOVE instruction. Chains via ENO for consecutive calls."""
        self.flush_pending_coils()
        src_uid = self.add_access(src_device)
        dst_uid = self.add_access(dst_device)
        part_uid = self.next_uid()

        self.parts_xml.append(
            f'    <Part Name="Move" UId="{part_uid}">\n'
            f'      <TemplateValue Name="Card" Type="Cardinality">1</TemplateValue>\n'
            f'    </Part>'
        )

        # EN wire from current_rail
        self._wire_to_dest(part_uid, 'en')

        # IN wire
        w = self.next_uid()
        self.wires_xml.append(
            f'    <Wire UId="{w}">\n'
            f'      <IdentCon UId="{src_uid}" />\n'
            f'      <NameCon UId="{part_uid}" Name="in" />\n'
            f'    </Wire>'
        )
        # OUT wire
        w = self.next_uid()
        self.wires_xml.append(
            f'    <Wire UId="{w}">\n'
            f'      <NameCon UId="{part_uid}" Name="out1" />\n'
            f'      <IdentCon UId="{dst_uid}" />\n'
            f'    </Wire>'
        )

        # Chain via ENO so consecutive moves don't duplicate source pin
        self.current_rail = (part_uid, 'eno')

    def add_arithmetic(self, op_name: str, operands: list[str]) -> None:
        """Add arithmetic operation (+, -, *)."""
        self.flush_pending_coils()
        if len(operands) < 3:
            return

        op1_uid = self.add_access(operands[0])
        op2_uid = self.add_access(operands[1])
        dst_uid = self.add_access(operands[2])
        part_uid = self.next_uid()

        self.parts_xml.append(
            f'    <Part Name="{op_name}" UId="{part_uid}">\n'
            f'      <TemplateValue Name="Card" Type="Cardinality">2</TemplateValue>\n'
            f'    </Part>'
        )

        # EN wire from current_rail
        self._wire_to_dest(part_uid, 'en')

        w = self.next_uid()
        self.wires_xml.append(
            f'    <Wire UId="{w}">\n'
            f'      <IdentCon UId="{op1_uid}" />\n'
            f'      <NameCon UId="{part_uid}" Name="in1" />\n'
            f'    </Wire>'
        )
        w = self.next_uid()
        self.wires_xml.append(
            f'    <Wire UId="{w}">\n'
            f'      <IdentCon UId="{op2_uid}" />\n'
            f'      <NameCon UId="{part_uid}" Name="in2" />\n'
            f'    </Wire>'
        )
        w = self.next_uid()
        self.wires_xml.append(
            f'    <Wire UId="{w}">\n'
            f'      <NameCon UId="{part_uid}" Name="out" />\n'
            f'      <IdentCon UId="{dst_uid}" />\n'
            f'    </Wire>'
        )

        # Chain via ENO
        self.current_rail = (part_uid, 'eno')

    def build_xml(self) -> str:
        """Return the FlgNet XML string."""
        self.flush_pending_coils()

        # Build single powerrail wire from all deferred connections
        active_pr = [d for d in self._pr_dests if d is not None]
        if active_pr:
            w = self.next_uid()
            lines = [f'    <Wire UId="{w}">', '      <Powerrail />']
            for uid, pin in active_pr:
                lines.append(f'      <NameCon UId="{uid}" Name="{pin}" />')
            lines.append('    </Wire>')
            # Insert powerrail wire at the beginning
            self.wires_xml.insert(0, '\n'.join(lines))

        parts = '\n'.join(self.parts_xml)
        wires = '\n'.join(self.wires_xml)
        return (
            f'<FlgNet xmlns="http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5">\n'
            f'  <Parts>\n{parts}\n  </Parts>\n'
            f'  <Wires>\n{wires}\n  </Wires>\n'
            f'</FlgNet>'
        )


# ── Compare operator mapping ──

COMPARE_MAP = {
    '=': 'Eq', '<>': 'Ne', '>=': 'Ge', '>': 'Gt', '<=': 'Le', '<': 'Lt',
}


def get_compare_op(instr: str) -> str | None:
    """Extract compare operator from instruction like 'LD=', 'AND>=', 'ANDD<'."""
    base = instr
    for prefix in ('LDD', 'ANDD', 'ORD', 'LD', 'AND', 'OR'):
        if base.startswith(prefix) and len(base) > len(prefix):
            op = base[len(prefix):]
            return COMPARE_MAP.get(op)
    return None


def is_compare_instr(instr: str) -> bool:
    return get_compare_op(instr) is not None


def is_ld_type(instr: str) -> bool:
    """Check if instruction starts a new rung (LD family)."""
    return (instr in ('LD', 'LDI', 'LDP', 'LDF')
            or instr.startswith('LD=') or instr.startswith('LD<')
            or instr.startswith('LD>') or instr.startswith('LDD'))


def is_or_type(instr: str) -> bool:
    """Check if instruction is OR-type (parallel)."""
    return (instr.startswith('OR') and instr not in ('ORB',)
            and instr.startswith('OR'))


# ── Main conversion logic ──

def convert_network(network: dict) -> str:
    """Convert a single network to FlgNet XML."""
    builder = LadNetworkBuilder()
    instrs = network['instructions']

    # Preprocess: resolve INV via De Morgan's law
    instrs = preprocess_inv(instrs)

    i = 0
    while i < len(instrs):
        instr, operands = instrs[i]

        # ── LD family (new rung / OR branch start) ──
        if instr == 'LD' and operands:
            if builder.current_rail is not None:
                builder.flush_pending_coils()
                builder.push_logic()
            builder.add_contact(operands[0])

        elif instr == 'LDI' and operands:
            if builder.current_rail is not None:
                builder.flush_pending_coils()
                builder.push_logic()
            builder.add_contact(operands[0], negated=True)

        elif instr == 'LDP' and operands:
            if builder.current_rail is not None:
                builder.flush_pending_coils()
                builder.push_logic()
            builder.add_contact(operands[0])  # simplified: no edge detection

        elif instr == 'LDF' and operands:
            if builder.current_rail is not None:
                builder.flush_pending_coils()
                builder.push_logic()
            builder.add_contact(operands[0])  # simplified: no edge detection

        # ── AND family (series) ──
        elif instr == 'AND' and operands:
            builder.add_contact(operands[0])
        elif instr == 'ANI' and operands:
            builder.add_contact(operands[0], negated=True)

        # ── OR single contact (parallel with implicit merge) ──
        elif instr == 'OR' and operands:
            old_rail = builder.current_rail
            builder.current_rail = None
            builder.add_contact(operands[0])
            builder.current_rail = LadNetworkBuilder._merge(old_rail, builder.current_rail)

        elif instr == 'ORI' and operands:
            old_rail = builder.current_rail
            builder.current_rail = None
            builder.add_contact(operands[0], negated=True)
            builder.current_rail = LadNetworkBuilder._merge(old_rail, builder.current_rail)

        elif instr == 'ORP' and operands:
            old_rail = builder.current_rail
            builder.current_rail = None
            builder.add_contact(operands[0])  # simplified
            builder.current_rail = LadNetworkBuilder._merge(old_rail, builder.current_rail)

        elif instr == 'ORF' and operands:
            old_rail = builder.current_rail
            builder.current_rail = None
            builder.add_contact(operands[0])  # simplified
            builder.current_rail = LadNetworkBuilder._merge(old_rail, builder.current_rail)

        # ── Edge detection (MEP/MEF) ──
        elif instr in ('MEP', 'MEF'):
            pass  # simplified: no-op

        # ── ORB: merge two parallel branches ──
        elif instr == 'ORB':
            builder.orb_merge()

        # ── ANB: series merge of two blocks ──
        elif instr == 'ANB':
            builder.anb_merge()

        # ── INV: should have been preprocessed away ──
        elif instr == 'INV':
            pass

        # ── MPS/MRD/MPP: stack operations ──
        elif instr == 'MPS':
            if builder.current_rail is not None:
                builder.branch_stack.append(builder.current_rail)
        elif instr == 'MRD':
            builder.flush_pending_coils()
            if builder.branch_stack:
                builder.current_rail = builder.branch_stack[-1]
        elif instr == 'MPP':
            builder.flush_pending_coils()
            if builder.branch_stack:
                builder.current_rail = builder.branch_stack.pop()

        # ── Compare instructions ──
        elif is_compare_instr(instr):
            op_name = get_compare_op(instr)
            if op_name and len(operands) >= 2:
                if is_ld_type(instr):
                    # LD-type compare: push logic for ORB/ANB
                    if builder.current_rail is not None:
                        builder.flush_pending_coils()
                        builder.push_logic()
                    builder.add_comparison(op_name, operands)
                elif is_or_type(instr):
                    # OR-type compare: parallel with implicit merge
                    old_rail = builder.current_rail
                    builder.current_rail = None
                    builder.add_comparison(op_name, operands)
                    builder.current_rail = LadNetworkBuilder._merge(old_rail, builder.current_rail)
                else:
                    # AND-type compare: series
                    builder.add_comparison(op_name, operands)

        # ── Output: OUT / SET / RST ──
        elif instr == 'OUT' and operands:
            builder.add_coil(operands[0], 'Coil')
        elif instr == 'SET' and operands:
            builder.add_coil(operands[0], 'SCoil')
        elif instr == 'RST' and operands:
            builder.add_coil(operands[0], 'RCoil')

        # ── MOVE instructions ──
        elif instr in ('MOV', 'MOVP', 'DMOV', 'DMOVP') and len(operands) >= 2:
            builder.add_move(operands[0], operands[1])

        elif instr == 'FMOVP' and len(operands) >= 3:
            builder.add_move(operands[0], operands[1])

        elif instr == 'BMOVP' and len(operands) >= 3:
            builder.add_move(operands[0], operands[1])

        # ── Arithmetic ──
        elif instr in ('+', '+P', 'D+', 'D-', 'D*') and len(operands) >= 3:
            arith_map = {'+': 'Add', '+P': 'Add', 'D+': 'Add', 'D-': 'Sub', 'D*': 'Mul'}
            builder.add_arithmetic(arith_map.get(instr, 'Add'), operands)

        # ── BKRST (block reset) ──
        elif instr == 'BKRST' and operands:
            builder.add_coil(operands[0], 'RCoil')

        # ── FOR/NEXT: skip ──
        elif instr in ('FOR', 'NEXT'):
            pass

        i += 1

    return builder.build_xml()


# ── XML template ──

OB_XML_HEADER = '''<?xml version="1.0" encoding="utf-8"?>
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
  <SW.Blocks.OB ID="0">
    <AttributeList>
      <AutoNumber>true</AutoNumber>
      <HeaderAuthor />
      <HeaderFamily />
      <HeaderName />
      <HeaderVersion>0.1</HeaderVersion>
      <Interface><Sections xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v5">
  <Section Name="Input">
    <Member Name="Initial_Call" Datatype="Bool" Accessibility="Public" Informative="true">
      <Comment>
        <MultiLanguageText Lang="en-US">Initial call of this OB</MultiLanguageText>
      </Comment>
    </Member>
    <Member Name="Remanence" Datatype="Bool" Accessibility="Public" Informative="true">
      <Comment>
        <MultiLanguageText Lang="en-US">True if remanent data are available</MultiLanguageText>
      </Comment>
    </Member>
  </Section>
  <Section Name="Temp" />
  <Section Name="Constant" />
</Sections></Interface>
      <IsIECCheckEnabled>true</IsIECCheckEnabled>
      <Name>{ob_name}</Name>
      <Namespace />
      <Number>{ob_number}</Number>
      <ProgrammingLanguage>LAD</ProgrammingLanguage>
      <SecondaryType>ProgramCycle</SecondaryType>
      <SetENOAutomatically>true</SetENOAutomatically>
    </AttributeList>
    <ObjectList>
      <MultilingualText ID="1" CompositionName="Comment">
        <ObjectList>
          <MultilingualTextItem ID="2" CompositionName="Items">
            <AttributeList>
              <Culture>en-US</Culture>
              <Text>{ob_comment}</Text>
            </AttributeList>
          </MultilingualTextItem>
        </ObjectList>
      </MultilingualText>
'''

OB_XML_FOOTER = '''    </ObjectList>
  </SW.Blocks.OB>
</Document>
'''


def build_compile_unit(network_idx: int, flgnet_xml: str, title: str, comment: str) -> str:
    """Build a single CompileUnit (network) XML."""
    # CompileUnit IDs: 3, 8, D, 12, ... (5 apart in hex)
    cu_id = format(3 + network_idx * 5, 'X')
    comment_id = format(3 + network_idx * 5 + 1, 'X')
    comment_item_id = format(3 + network_idx * 5 + 2, 'X')
    title_id = format(3 + network_idx * 5 + 3, 'X')
    title_item_id = format(3 + network_idx * 5 + 4, 'X')

    return (
        f'      <SW.Blocks.CompileUnit ID="{cu_id}" CompositionName="CompileUnits">\n'
        f'        <AttributeList>\n'
        f'          <NetworkSource>{flgnet_xml}</NetworkSource>\n'
        f'          <ProgrammingLanguage>LAD</ProgrammingLanguage>\n'
        f'        </AttributeList>\n'
        f'        <ObjectList>\n'
        f'          <MultilingualText ID="{comment_id}" CompositionName="Comment">\n'
        f'            <ObjectList>\n'
        f'              <MultilingualTextItem ID="{comment_item_id}" CompositionName="Items">\n'
        f'                <AttributeList>\n'
        f'                  <Culture>en-US</Culture>\n'
        f'                  <Text>{escape(comment)}</Text>\n'
        f'                </AttributeList>\n'
        f'              </MultilingualTextItem>\n'
        f'            </ObjectList>\n'
        f'          </MultilingualText>\n'
        f'          <MultilingualText ID="{title_id}" CompositionName="Title">\n'
        f'            <ObjectList>\n'
        f'              <MultilingualTextItem ID="{title_item_id}" CompositionName="Items">\n'
        f'                <AttributeList>\n'
        f'                  <Culture>en-US</Culture>\n'
        f'                  <Text>{escape(title)}</Text>\n'
        f'                </AttributeList>\n'
        f'              </MultilingualTextItem>\n'
        f'            </ObjectList>\n'
        f'          </MultilingualText>\n'
        f'        </ObjectList>\n'
        f'      </SW.Blocks.CompileUnit>\n'
    )


def convert_file(input_file: str, output_file: str, ob_name: str = None, ob_number: int = 123):
    """Convert a Mitsubishi ladder CSV to TIA OB XML."""
    rows = read_ladder_csv(input_file)
    networks = parse_networks(rows)

    if ob_name is None:
        ob_name = Path(input_file).stem

    print(f'Parsed {len(networks)} networks from {input_file}')

    xml = OB_XML_HEADER.format(
        ob_name=escape(ob_name),
        ob_number=ob_number,
        ob_comment=escape(f'Converted from {Path(input_file).name}'),
    )

    for idx, network in enumerate(networks):
        title = network.get('title', '') or f'Network {idx + 1}'
        comment = network.get('comment', '')
        try:
            flgnet = convert_network(network)
            xml += build_compile_unit(idx, flgnet, title, comment)
        except Exception as e:
            print(f'  Warning: Network {idx + 1} "{title}" failed: {e}')
            continue

    xml += OB_XML_FOOTER

    Path(output_file).write_text(xml, encoding='utf-8')
    print(f'Saved to {output_file}')


def main():
    if len(sys.argv) < 2:
        print('Usage: python convert_ladder_to_ob.py <ladder.csv> [output.xml]')
        sys.exit(1)

    input_file = sys.argv[1]
    output_file = sys.argv[2] if len(sys.argv) > 2 else input_file.rsplit('.', 1)[0] + '_ob.xml'

    convert_file(input_file, output_file)


if __name__ == '__main__':
    main()
