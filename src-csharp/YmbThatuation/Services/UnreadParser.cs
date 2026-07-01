using System.Text.RegularExpressions;

namespace YmbThatuation.Services;

/// <summary>
/// ウインドウタイトルの "(N)" 表記から未読件数を取得する処理と、
/// 各サービスページに注入して未読バッジ表示をタイトルに反映させるスクリプト。
/// Rust版 parse_unread / build_injected_script の移植。
/// </summary>
public static class UnreadParser
{
    // 先頭アンカーは付けない。Gmailの実タイトルは"Inbox (3) - Gmail"のように未読数が
    // 先頭に来ないため(task #53で確認済み)、アンカー化すると既存の検知が壊れる。
    private static readonly Regex UnreadRegex = new(@"\((\d+)\)", RegexOptions.Compiled);

    public static uint Parse(string title)
    {
        var match = UnreadRegex.Match(title);
        if (match.Success && uint.TryParse(match.Groups[1].Value, out var n))
        {
            return n;
        }
        return 0;
    }

    public const string InjectedScript = @"(function(){
  if (window.__ymbUnreadScanStarted) return;
  window.__ymbUnreadScanStarted = true;
  function scan() {
    try {
      // ページ自身が既にタイトルに未読数""(N)""を含めている場合(Gmail等)は
      // それを正として扱い、DOM走査による上書きは行わない。
      if (/\(\d+\)/.test(document.title)) return;

      // タイトルに未読数を出さないサービス(Teams/Discord系UI等)向けのフォールバック。
      // class/aria-label/data-testidに""unread""や""badge""/""count""を含む要素のうち、
      // 中身が短い数字のみのものを未読バッジ候補とみなす(誤検知抑制のため3桁までに制限)。
      var els = document.querySelectorAll(
        '[class*=""unread"" i], [aria-label*=""unread"" i], [aria-label*=""未読"" i], ' +
        '[data-testid*=""unread"" i], [class*=""badge"" i], [class*=""Badge""], ' +
        '[data-testid*=""badge"" i], [class*=""notification-count"" i]'
      );
      var best = 0;
      for (var i = 0; i < els.length; i++) {
        var t = (els[i].textContent || '').trim();
        if (/^\d{1,3}$/.test(t)) {
          var n = parseInt(t, 10);
          if (n > best) best = n;
        }
      }
      var base = window.__ymbBaseTitle || document.title;
      window.__ymbBaseTitle = base;
      var wanted = best > 0 ? '(' + best + ') ' + base : base;
      if (document.title !== wanted) document.title = wanted;
    } catch (e) {}
  }
  setInterval(scan, 5000);
  scan();
})();";
}
