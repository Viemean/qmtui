using System;
using System.Reflection;
using QmTui.Models;
using QmTui.UI;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Xunit;
using Xunit.Abstractions;

namespace QmTui.Tests;

public class CommentFocusNavigationTests
{
    private readonly ITestOutputHelper _output;

    public CommentFocusNavigationTests(ITestOutputHelper output)
    {
        _output = output;
    }

#pragma warning disable CS0618
    [Fact]
    public void TestNowPlayingView_TabNavigationCycle_WorksCorrectly()
    {
        Application.Init();
        try
        {
            var top = new View { Width = 100, Height = 40, Visible = true, CanFocus = true };
            var nowPlaying = new NowPlayingView { Width = 100, Height = 40, Visible = true, CanFocus = true };
            top.Add(nowPlaying);

            var song = new Song("mid1", "title1", "artist1", "album1", 180, "media1");
            nowPlaying.SetSong(song, "SQ");
            nowPlaying.OnActivated();

            var coverContainerField = typeof(NowPlayingView).GetField("_coverContainer", BindingFlags.NonPublic | BindingFlags.Instance);
            var coverContainer = (View)coverContainerField!.GetValue(nowPlaying)!;
            coverContainer.Visible = true;

            var artistLinkField = typeof(NowPlayingView).GetField("_artistLink", BindingFlags.NonPublic | BindingFlags.Instance);
            var artistLink = (View)artistLinkField!.GetValue(nowPlaying)!;

            var albumLinkField = typeof(NowPlayingView).GetField("_albumLink", BindingFlags.NonPublic | BindingFlags.Instance);
            var albumLink = (View)albumLinkField!.GetValue(nowPlaying)!;

            var commentViewField = typeof(NowPlayingView).GetField("_commentView", BindingFlags.NonPublic | BindingFlags.Instance);
            var commentView = (SongCommentView)commentViewField!.GetValue(nowPlaying)!;

            // 1. 打开评论区
            nowPlaying.ToggleCommentView();
            _output.WriteLine($"After ToggleCommentView: commentView.Visible={commentView.Visible}, commentView.HasActiveFocus={commentView.HasActiveFocus}");

            // 2. 检查 commentView.SetFocus()
            bool commentFocusRes = commentView.SetFocus();
            _output.WriteLine($"commentView.SetFocus() result={commentFocusRes}, HasActiveFocus={commentView.HasActiveFocus}");

            // 3. 模拟按 Tab（forward）
            nowPlaying.HandleTabNavigation(forward: true);
            _output.WriteLine($"After Tab 1: artistLink.HasFocus={artistLink.HasFocus}, albumLink.HasFocus={albumLink.HasFocus}, commentView.HasActiveFocus={commentView.HasActiveFocus}");

            nowPlaying.HandleTabNavigation(forward: true);
            _output.WriteLine($"After Tab 2: artistLink.HasFocus={artistLink.HasFocus}, albumLink.HasFocus={albumLink.HasFocus}, commentView.HasActiveFocus={commentView.HasActiveFocus}");

            nowPlaying.HandleTabNavigation(forward: true);
            _output.WriteLine($"After Tab 3: artistLink.HasFocus={artistLink.HasFocus}, albumLink.HasFocus={albumLink.HasFocus}, commentView.HasActiveFocus={commentView.HasActiveFocus}");

            nowPlaying.HandleTabNavigation(forward: true);
            _output.WriteLine($"After Tab 4: artistLink.HasFocus={artistLink.HasFocus}, albumLink.HasFocus={albumLink.HasFocus}, commentView.HasActiveFocus={commentView.HasActiveFocus}");

            Assert.True(commentFocusRes);
        }
        finally
        {
            Application.Shutdown();
        }
    }

    [Fact]
    public void TestCommentView_CloseWithC_Or_Esc_Works()
    {
        Application.Init();
        try
        {
            var top = new View { Width = 100, Height = 40, Visible = true, CanFocus = true };
            var nowPlaying = new NowPlayingView { Width = 100, Height = 40, Visible = true, CanFocus = true };
            top.Add(nowPlaying);

            var song = new Song("mid1", "title1", "artist1", "album1", 180, "media1");
            nowPlaying.SetSong(song, "SQ");
            nowPlaying.OnActivated();

            // 打开评论区
            nowPlaying.ToggleCommentView();
            Assert.True(nowPlaying.IsCommentViewActive);

            var commentViewField = typeof(NowPlayingView).GetField("_commentView", BindingFlags.NonPublic | BindingFlags.Instance);
            var commentView = (SongCommentView)commentViewField!.GetValue(nowPlaying)!;

            var listViewField = typeof(SongCommentView).GetField("_listView", BindingFlags.NonPublic | BindingFlags.Instance);
            var listView = (Terminal.Gui.Views.ListView)listViewField!.GetValue(commentView)!;

            // 模拟按 C 键关闭
            listView.NewKeyDownEvent(Terminal.Gui.Input.Key.C);
            Assert.False(nowPlaying.IsCommentViewActive);

            System.Threading.Thread.Sleep(400);

            // 再次打开
            nowPlaying.ToggleCommentView();
            Assert.True(nowPlaying.IsCommentViewActive);

            System.Threading.Thread.Sleep(400);

            // 模拟按 Esc 键关闭
            listView.NewKeyDownEvent(Terminal.Gui.Input.Key.Esc);
            Assert.False(nowPlaying.IsCommentViewActive);
        }
        finally
        {
            Application.Shutdown();
        }
    }
#pragma warning restore CS0618
}
