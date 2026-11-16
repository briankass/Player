using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media;

namespace GlassPlayer.Models;

/// <summary>One file in the metadata fixer: what's in the file now, and what we propose to write.</summary>
public class FixItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public string Path { get; set; } = "";
    public string FileName => System.IO.Path.GetFileName(Path);

    public string CurTitle { get; set; } = "";
    public string CurArtist { get; set; } = "";
    public string CurAlbum { get; set; } = "";
    public uint CurYear { get; set; }
    public uint CurTrack { get; set; }
    public bool CurHasArt { get; set; }
    public double Duration { get; set; }

    // search hints (tags, or guessed from the file/folder names)
    public string HintTitle { get; set; } = "";
    public string HintArtist { get; set; } = "";
    public string HintAlbum { get; set; } = "";

    public string? NewTitle { get; set; }
    public string? NewArtist { get; set; }
    public string? NewAlbum { get; set; }
    public uint? NewYear { get; set; }
    public uint? NewTrack { get; set; }
    public byte[]? NewArt { get; set; }

    public bool HasChanges => NewTitle != null || NewArtist != null || NewAlbum != null
                              || NewYear != null || NewTrack != null || NewArt != null;

    public string MissingText
    {
        get
        {
            var m = new List<string>();
            if (CurTitle.Length == 0) m.Add("title");
            if (CurArtist.Length == 0) m.Add("artist");
            if (CurAlbum.Length == 0) m.Add("album");
            if (CurYear == 0) m.Add("year");
            if (CurTrack == 0) m.Add("track #");
            if (!CurHasArt) m.Add("art");
            return m.Count == 0 ? "Nothing missing" : "Missing: " + string.Join(", ", m);
        }
    }

    public string CurrentText =>
        $"Now: {Show(CurArtist)} — {Show(CurAlbum)} — {Show(CurTitle)}"
        + (CurYear > 0 ? $" ({CurYear})" : "") + (CurTrack > 0 ? $" #{CurTrack}" : "");

    private static string Show(string s) => s.Length == 0 ? "?" : s;

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set { if (_isSelected != value) { _isSelected = value; Changed(); } } }

    private string _status = "Not searched yet";
    public string Status { get => _status; set { _status = value; Changed(); } }

    private string _proposedText = "";
    public string ProposedText { get => _proposedText; set { _proposedText = value; Changed(); } }

    private ImageSource? _artPreview;
    public ImageSource? ArtPreview { get => _artPreview; set { _artPreview = value; Changed(); } }

    public void RefreshProposed()
    {
        if (!HasChanges) { ProposedText = ""; return; }
        var parts = new List<string>();
        if (NewArtist != null) parts.Add($"artist → {NewArtist}");
        if (NewAlbum != null) parts.Add($"album → {NewAlbum}");
        if (NewTitle != null) parts.Add($"title → {NewTitle}");
        if (NewYear != null) parts.Add($"year → {NewYear}");
        if (NewTrack != null) parts.Add($"track → {NewTrack}");
        if (NewArt != null) parts.Add("cover art");
        ProposedText = "Will set: " + string.Join(" · ", parts);
    }
}
