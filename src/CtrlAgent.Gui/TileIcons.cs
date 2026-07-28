using Avalonia.Media;

namespace CtrlAgent.Gui;

/// <summary>
/// Stroked icon geometry for the Mainframe tiles (18×18 grid, drawn by the
/// shared <c>Path.icon</c> style). Code-drawn for the same reason every other
/// icon in the app is: the bundled font has no emoji or dingbats, and the
/// Windows fallback renders them as monochrome blobs.
/// </summary>
public static class TileIcons
{
    public static readonly Geometry Shield = Geometry.Parse(
        "M9,2.5 L15,4.8 V9 C15,12.6 12.6,15 9,16 C5.4,15 3,12.6 3,9 V4.8 Z");

    public static readonly Geometry Chip = Geometry.Parse(
        "M5,5 H13 V13 H5 Z M7,2.5 V5 M11,2.5 V5 M7,13 V15.5 M11,13 V15.5 " +
        "M2.5,7 H5 M2.5,11 H5 M13,7 H15.5 M13,11 H15.5");

    public static readonly Geometry Bolt = Geometry.Parse(
        "M10,2.5 L4.5,10 H8.5 L8,15.5 L13.5,8 H9.5 Z");

    public static readonly Geometry Compress = Geometry.Parse(
        "M9,3 V7.2 M6.8,5.4 L9,7.6 L11.2,5.4 M9,15 V10.8 M6.8,12.6 L9,10.4 L11.2,12.6 M4.2,9 H13.8");

    public static readonly Geometry Paperclip = Geometry.Parse(
        "M12.6,5.2 L6.4,11.4 A2.4,2.4 0 0 0 9.8,14.8 L15.2,9.4 " +
        "A4,4 0 0 0 9.6,3.8 L4.2,9.2 A5.6,5.6 0 0 0 12.2,17.2");

    public static readonly Geometry Microphone = Geometry.Parse(
        "M9,2.5 A2.6,2.6 0 0 1 11.6,5.1 L11.6,9.4 A2.6,2.6 0 0 1 6.4,9.4 " +
        "L6.4,5.1 A2.6,2.6 0 0 1 9,2.5 Z M4.2,9.2 A4.8,4.8 0 0 0 13.8,9.2 " +
        "M9,14 L9,16.2 M6.4,16.2 L11.6,16.2");

    public static readonly Geometry Folder = Geometry.Parse(
        "M2.5,14.5 V4.5 H7 L8.6,6.4 H15.5 V14.5 Z");

    public static readonly Geometry Gear = Geometry.Parse(
        "M9,5.6 A3.4,3.4 0 1 1 8.99,5.6 M9,2.4 V4.4 M9,13.6 V15.6 M2.4,9 H4.4 M13.6,9 H15.6 " +
        "M4.3,4.3 L5.7,5.7 M12.3,12.3 L13.7,13.7 M13.7,4.3 L12.3,5.7 M5.7,12.3 L4.3,13.7");

    public static readonly Geometry Gamepad = Geometry.Parse(
        "M5.5,6 H12.5 C14.8,6 15.8,8 15.4,10.6 C15.1,12.4 13.4,12.9 12.3,11.8 L11.3,10.8 H6.7 " +
        "L5.7,11.8 C4.6,12.9 2.9,12.4 2.6,10.6 C2.2,8 3.2,6 5.5,6 Z " +
        "M6,8.9 H8 M7,7.9 V9.9 M11.6,8.2 L11.61,8.2 M13,9.7 L13.01,9.7");

    public static readonly Geometry Eject = Geometry.Parse(
        "M4.5,10.5 L9,5.5 L13.5,10.5 Z M4.5,13.5 H13.5");

    public static readonly Geometry Diff = Geometry.Parse(
        "M5,3.5 H3 V14.5 H5 M13,3.5 H15 V14.5 H13 M9,5.5 V9.5 M7,7.5 H11 M7,12 H11");
}
