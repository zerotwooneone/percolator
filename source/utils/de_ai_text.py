# /// script
# requires-python = ">=3.10"
# dependencies = [
#     "rich",
# ]
# ///

import argparse
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Final

from rich.console import Console
from rich.table import Table

# Initialize rich consoles (one for stdout, one for stderr)
console = Console()
err_console = Console(stderr=True)

# Map of common typographical/AI-injected characters to ASCII equivalents
TYPOGRAPHY_MAP: Final[dict[str, str]] = {
    '\u2018': "'",   # ‘ Left single quote
    '\u2019': "'",   # ’ Right single quote
    '\u201A': "'",   # ‚ Single low-9 quote
    '\u201C': '"',   # “ Left double quote
    '\u201D': '"',   # ” Right double quote
    '\u201E': '"',   # „ Double low-9 quote
    '\u2013': "-",   # – En dash
    '\u2014': "--",  # — Em dash
    '\u2026': "...", # … Horizontal ellipsis
    '\u00A0': " ",   # Non-breaking space
    '\u200B': "",    # Zero-width space (invisible)
    '\u2022': "*",   # • Bullet
    '\u00D7': "x",   # × Multiplication sign
    '\u00F7': "/",   # ÷ Division sign
}

# Pre-compile the translation table for extremely fast C-level replacements
TRANSLATION_TABLE: Final = str.maketrans(TYPOGRAPHY_MAP)

# 50 MB limit warning to prevent accidental memory exhaustion on massive logs/binaries
MAX_SAFE_FILE_SIZE_BYTES: Final[int] = 50 * 1024 * 1024 


@dataclass
class Anomaly:
    """Represents a non-ASCII character found in the text."""
    line: int
    col: int
    char: str
    unicode_hex: str
    replacement: str | None = None


def scan_text(content: str) -> tuple[list[Anomaly], list[Anomaly]]:
    """
    Scans a string and categorizes non-ASCII characters.
    
    Returns:
        A tuple of (mapped_anomalies, unmapped_anomalies).
    """
    mapped_found: list[Anomaly] = []
    unmapped_found: list[Anomaly] = []

    for line_idx, line in enumerate(content.splitlines(), start=1):
        for col_idx, char in enumerate(line, start=1):
            if ord(char) > 127:  # Non-ASCII threshold
                uni_hex = f"U+{ord(char):04X}"
                
                if char in TYPOGRAPHY_MAP:
                    mapped_found.append(
                        Anomaly(line_idx, col_idx, char, uni_hex, TYPOGRAPHY_MAP[char])
                    )
                else:
                    unmapped_found.append(
                        Anomaly(line_idx, col_idx, char, uni_hex)
                    )

    return mapped_found, unmapped_found


def print_report(file_path: Path, mapped: list[Anomaly], unmapped: list[Anomaly]) -> None:
    """Prints a formatted report of the anomalies found."""
    if not mapped and not unmapped:
        console.print(f"[bold green]✓ '{file_path.name}' is clean. No non-ASCII characters found.[/bold green]")
        return

    if mapped:
        table = Table(title=f"Known Typographical Characters in {file_path.name}", header_style="bold cyan")
        table.add_column("Line", justify="right", style="dim")
        table.add_column("Col", justify="right", style="dim")
        table.add_column("Character", justify="center", style="bold yellow")
        table.add_column("Unicode", style="magenta")
        table.add_column("Suggested ASCII Fix", style="green")

        for item in mapped:
            char_display = "[invisible]" if item.char in ['\u00A0', '\u200B'] else item.char
            replacement_display = "[remove]" if item.replacement == "" else str(item.replacement)
            
            table.add_row(
                str(item.line), 
                str(item.col), 
                char_display, 
                item.unicode_hex, 
                replacement_display
            )
        console.print(table)

    if unmapped:
        console.print("\n[bold yellow]⚠ Unmapped Non-ASCII Characters Found:[/bold yellow] (These will NOT be auto-fixed)")
        for item in unmapped:
            console.print(f"  [dim]Line {item.line}, Col {item.col}:[/dim] '{item.char}' ({item.unicode_hex})")
            
    console.print(f"\n[dim]Run with [/dim][bold cyan]--fix[/bold cyan][dim] to automatically apply replacements.[/dim]")


def process_file(file_path: Path, apply_fix: bool) -> int:
    """
    Reads, scans, reports, and optionally fixes the file.
    
    Returns:
        Exit code (0 for success, 1 for failure).
    """
    # 1. Safely check file existence and permissions
    if not file_path.exists():
        err_console.print(f"[bold red]Error:[/bold red] Path '{file_path}' does not exist.")
        return 1
    if not file_path.is_file():
        err_console.print(f"[bold red]Error:[/bold red] '{file_path}' is a directory, not a file.")
        return 1

    try:
        if file_path.stat().st_size > MAX_SAFE_FILE_SIZE_BYTES:
            err_console.print(f"[bold yellow]Warning:[/bold yellow] File exceeds 50MB. This may consume significant memory.")
    except OSError as e:
        err_console.print(f"[bold red]Error accessing file stats:[/bold red] {e}")
        return 1

    # 2. Read the file
    try:
        content = file_path.read_text(encoding='utf-8')
    except UnicodeDecodeError:
        err_console.print(f"[bold red]Error:[/bold red] Could not decode '{file_path.name}' as UTF-8. Is it a binary file?")
        return 1
    except PermissionError:
        err_console.print(f"[bold red]Error:[/bold red] Permission denied to read '{file_path}'.")
        return 1
    except OSError as e:
        err_console.print(f"[bold red]Unexpected I/O error reading file:[/bold red] {e}")
        return 1

    # 3. Scan the text
    console.print(f"Scanning [bold]{file_path.name}[/bold]...", style="dim")
    mapped, unmapped = scan_text(content)

    # 4. Report or Fix
    if apply_fix:
        if not mapped:
            console.print(f"[bold green]✓ No known typographical characters to fix in '{file_path.name}'.[/bold green]")
        else:
            # Use str.translate for maximum performance and preservation of line endings
            fixed_content = content.translate(TRANSLATION_TABLE)
            
            try:
                file_path.write_text(fixed_content, encoding='utf-8')
                console.print(f"[bold green]✓ Fixed {len(mapped)} anomalies in '{file_path.name}'.[/bold green]")
                if unmapped:
                    console.print(f"[yellow]Note: {len(unmapped)} unmapped non-ASCII characters were left untouched.[/yellow]")
            except PermissionError:
                err_console.print(f"[bold red]Error:[/bold red] Permission denied to write to '{file_path}'.")
                return 1
            except OSError as e:
                err_console.print(f"[bold red]Unexpected I/O error writing file:[/bold red] {e}")
                return 1
    else:
        print_report(file_path, mapped, unmapped)

    return 0


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Scan and gracefully fix AI-injected typographical characters in text files.",
        formatter_class=argparse.ArgumentDefaultsHelpFormatter
    )
    parser.add_argument(
        "file", 
        type=Path, 
        help="Path to the file to scan."
    )
    parser.add_argument(
        "--fix", 
        action="store_true", 
        help="Apply ASCII replacements to the file (destructive)."
    )
    
    args = parser.parse_args()
    
    # Process the file and exit with the appropriate system code
    exit_code = process_file(args.file, args.fix)
    sys.exit(exit_code)


if __name__ == "__main__":
    main()