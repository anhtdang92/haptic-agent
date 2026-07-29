using System.ComponentModel;
using System.Windows.Input;

namespace CtrlAgent.Gui;

/// <summary>
/// One file in the diff panel's rail: its headline ("M src/App.cs · +3 −1")
/// and a click that jumps the row list to that file. <see cref="IsCurrent"/>
/// tracks the last file jumped to — by click or by d-pad step — so the rail
/// shows where in the diff you are.
/// </summary>
public sealed class DiffFileLink(string headline, ICommand jump) : INotifyPropertyChanged
{
    private bool _isCurrent;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Headline { get; } = headline;

    public ICommand Jump { get; } = jump;

    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent != value)
            {
                _isCurrent = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCurrent)));
            }
        }
    }
}
