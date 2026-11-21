// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

namespace Percolator.Desktop.Features.Chats.Models;

using System;
using System.Collections.Generic;

/// <summary>
/// Volatile in-memory draft state preserving unsent composer text, cursor position,
/// attachments, and expiration timer settings across chat switches.
/// </summary>
public sealed record ChatDraftModel(
    string Text,
    int CursorPosition,
    IReadOnlyList<string> Attachments,
    int? ExpireAfterSeconds);
