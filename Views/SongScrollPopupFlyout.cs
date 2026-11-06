using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;

namespace TrueFluentPro.Views;

/// <summary>Long-scroll flyouts use screen DIPs, not the book's design-pixel transform.</summary>
public sealed class SongScrollPopupFlyout : Flyout
{
    public SongScrollPopupFlyout()
    {
        Popup.InheritsTransform = false;
        FlyoutPresenterClasses.Add("scroll-popup");
    }

    protected override Control CreatePresenter()
    {
        var presenter = base.CreatePresenter();
        // Popup content is reparented. Keep its palette and typography on the presenter
        // itself, after page styles, rather than relying on a SongScrollShell ancestor.
        presenter.Name = "SongScrollPopupPresenter";
        presenter.Styles.Add(new StyleInclude(new Uri("avares://TrueFluentPro/"))
        {
            Source = new Uri("avares://TrueFluentPro/Styles/SongScrollPopup.axaml")
        });
        return presenter;
    }
}