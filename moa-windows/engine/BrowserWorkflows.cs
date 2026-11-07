using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace VoltiEngine;

public sealed record BrowserElement(int Number, string Label, string Kind, string Href, string InputType, bool InForm, IElementHandle Handle);
public sealed record BrowserView(IPage Page, string Url, DateTimeOffset ObservedAt, BrowserElement[] Elements)
{ public bool Used { get; set; } }

// Explicit native commands only. DOM text is observation, never executable instructions.
public sealed class BrowserWorkflows(EngineOptions options, SiteRegistry sites) : IAsyncDisposable
{
    private IPlaywright? playwright;
    private IBrowserContext? browser;
    private bool Allowed(string url) => Uri.TryCreate(url, UriKind.Absolute, out var target) && target.Scheme is "https" or "http" &&
        target.UserInfo.Length == 0 && sites.List().Any(s => new Uri(s.Url).GetLeftPart(UriPartial.Authority).Equals(target.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase));
    private async Task<IBrowserContext> Ensure()
    {
        if (browser is not null && browser.Pages.Any(p => !p.IsClosed)) return browser;
        if (browser is not null) { try { await browser.CloseAsync(); } catch (PlaywrightException) { } browser = null; }
        playwright ??= await Playwright.CreateAsync();
        browser = await playwright.Chromium.LaunchPersistentContextAsync(Path.Combine(options.DataPath, "browser-profile"), new()
        { Channel = "chrome", Headless = false, ChromiumSandbox = true, AcceptDownloads = false, ViewportSize = null, Timeout = 20000 });
        browser.SetDefaultTimeout(6000); browser.SetDefaultNavigationTimeout(15000);
        await browser.RouteAsync("**/*", async route =>
        {
            // A new popup's first request can arrive before its Frame exists.
            if (route.Request.IsNavigationRequest &&
                (route.Request.Method != "GET" || !Allowed(route.Request.Url))) await route.AbortAsync();
            else await route.ContinueAsync();
        });
        browser.Page += (_, page) => page.Dialog += async (_, dialog) => { try { await dialog.DismissAsync(); } catch (PlaywrightException) { } };
        return browser;
    }
    public static bool IsBrowserCommand(string text) => text.StartsWith("/웹") || Regex.IsMatch(text,
        @"^(?:사이트|웹|브라우저)\s*(?:화면|에서|뒤로|새로|닫)|^.+?\s*조작(?:창)?(?:으로)?\s*(?:열어\s*줘|해\s*줘)|^.+?에서\s*.+?\s*(?:검색해|찾아|눌러|클릭해|열어)\s*줘");
    public async Task<string?> TryHandle(string text, bool native, SkillContext context, CancellationToken token)
    {
        if (!IsBrowserCommand(text)) return null;
        if (!native) throw new ApiFailure(403, "native_only", "사이트 조작은 Windows 모아에서 해 줘.");
        token.ThrowIfCancellationRequested();
        try
        {
            if (text.StartsWith("/웹 결제내역 "))
            {
                var finance = sites.Find("금융");
                if (finance.Length != 1) return "모의 결제는 저장했지만 금융 사이트를 찾지 못했어요. ‘모의 구매 기록 보여줘’로 확인해 주세요.";
                var payload = text["/웹 결제내역 ".Length..].Trim();
                using var receipt = JsonDocument.Parse(Uri.UnescapeDataString(payload));
                if (!receipt.RootElement.TryGetProperty("demo", out var demo) || !demo.GetBoolean()) return "모의 결제 영수증 형식이 아니에요.";
                var session = await Ensure(); var page = await session.NewPageAsync();
                var url = new Uri(new Uri(finance[0].Url), "receipt.html").AbsoluteUri + "#moa=" + payload;
                var response = await page.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
                if (response is null || response.Status >= 400) return "모의 결제는 저장했지만 금융 화면을 열지 못했어요. ‘모의 구매 기록 보여줘’로 확인해 주세요.";
                await page.Locator("#content:not([hidden])").WaitForAsync(new() { Timeout = 8000 });
                return Compact("금융 화면에 모의 결제 내역을 표시했어요.", await Observe(page, context));
            }
            var open = Regex.Match(text, @"^(?:/웹\s+열기\s+(?<name>.+)|(?<name>.+?)\s*조작(?:창)?(?:으로)?\s*(?:열어\s*줘|해\s*줘))[.!?]*$");
            var siteAction = Regex.Match(text, @"^(?<site>.+?)에서\s*(?<target>.+?)\s*(?<action>검색해|찾아|눌러|클릭해|열어)\s*줘[.!?]*$");
            if (open.Success || (siteAction.Success && siteAction.Groups["site"].Value is not ("사이트" or "웹" or "브라우저")))
            {
                string name = (open.Success ? open.Groups["name"].Value : siteAction.Groups["site"].Value).Trim();
                var matches = sites.Find(name);
                if (matches.Length == 0 && !open.Success) return null;
                if (matches.Length != 1) return matches.Length == 0 ? "등록한 사이트 이름으로 말해 줘. 예: ‘갈피 조작해줘’." : "사이트가 여러 개예요. 이름을 더 정확히 골라 줘: " + string.Join(", ", matches.Select(s => s.Name));
                var target = matches[0];
                if (context.Browser is null || context.Browser.Page.IsClosed || new Uri(context.Browser.Page.Url).GetLeftPart(UriPartial.Authority) != new Uri(target.Url).GetLeftPart(UriPartial.Authority))
                {
                    var session = await Ensure(); var page = await session.NewPageAsync();
                    var response = await page.GotoAsync(target.Url, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
                    if (response?.Status is 401 or 403)
                    {
                        await page.CloseAsync(); context.Browser = null; context.BrowserConversation = false;
                        return target.Name + " 사이트는 로그인 또는 접근 권한이 필요해 내용을 확인하지 못했어요.\n" + target.Url +
                            "\n‘기본 크롬으로 " + target.Name + " 열어줘’로 계정이 있는 브라우저에서 열 수 있어요.";
                    }
                    await Observe(page, context);
                }
                if (open.Success) return Compact("모아 전용 크롬에서 열었어요.", await Observe(context.Browser!.Page, context));
            }
            if (context.Browser is null || context.Browser.Page.IsClosed) return "먼저 ‘갈피 조작해줘’처럼 조작할 사이트를 열어 줘.";
            var current = context.Browser.Page;
            if (text is "/웹 화면" or "사이트 화면 보여줘" or "웹 화면 보여줘" or "브라우저 화면 보여줘") return await Observe(current, context);
            if (text is "/웹 닫기" or "사이트 닫아줘" or "브라우저 닫아줘") { await current.CloseAsync(); context.Browser = null; context.BrowserConversation = false; return "모아가 조작하던 사이트 탭을 닫았어요."; }
            if (text is "/웹 뒤로" or "사이트 뒤로 가줘" or "브라우저 뒤로 가줘") { await current.GoBackAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded }); return await Observe(current, context); }
            if (text is "/웹 새로고침" or "사이트 새로고침해줘") { await current.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded }); return await Observe(current, context); }
            var search = Regex.Match(text, @"^/웹\s+검색\s+(.+)$");
            if (search.Success || (siteAction.Success && siteAction.Groups["action"].Value is "검색해" or "찾아"))
            {
                string query = search.Success ? search.Groups[1].Value : siteAction.Groups["target"].Value;
                await Observe(current, context);
                var fields = context.Browser!.Elements.Where(SearchField).ToArray();
                // SPA pages may finish DOMContentLoaded before rendering their search UI.
                // Retry observations only; navigation or filling is never repeated blindly.
                var readyUntil = DateTimeOffset.UtcNow.AddSeconds(8);
                bool openedLibrary = false;
                while (fields.Length == 0 && DateTimeOffset.UtcNow < readyUntil)
                {
                    token.ThrowIfCancellationRequested();
                    if (!openedLibrary && new Uri(current.Url).Host == "galpi-ai-studio.vercel.app")
                    {
                        var menu = context.Browser!.Elements.Where(e => e.Kind == "link" && e.Label == "자료 탐색").ToArray();
                        if (menu.Length == 1)
                        {
                            openedLibrary = true;
                            await Click(menu[0], context, token); current = context.Browser!.Page;
                        }
                    }
                    await Task.Delay(150, token);
                    await Observe(current, context);
                    fields = context.Browser!.Elements.Where(SearchField).ToArray();
                }
                if (fields.Length != 1) return "검색칸을 하나로 고르지 못했어요. ‘사이트 화면 보여줘’로 확인해 줘.";
                await Fill(fields[0], query, context, token);
                return Compact("검색칸에 입력했어요. 지금 보이는 결과예요.", await Observe(current, context));
            }
            var fill = Regex.Match(text, @"^/웹\s+입력\s+w?(\d+)\s+(.+)$");
            if (fill.Success)
            {
                var element = Select(fill.Groups[1].Value, context);
                await Fill(element, fill.Groups[2].Value, context, token);
                return Compact("검색칸에 입력했어요.", await Observe(current, context));
            }
            var click = Regex.Match(text, @"^/웹\s+클릭\s+(.+)$");
            if (click.Success || siteAction.Success)
            {
                var target = click.Success ? click.Groups[1].Value : siteAction.Groups["target"].Value;
                // Names can be resolved against a fresh view. Numbered references must
                // keep the existing snapshot/staleness contract to avoid clicking a new item.
                if (siteAction.Success && !Regex.IsMatch(target, @"^w?\d+$")) await Observe(current, context);
                var element = Select(target, context);
                await Click(element, context, token);
                return Compact("눌렀어요. 지금 열린 화면이에요.", await Observe(context.Browser!.Page, context));
            }
            return "‘갈피 조작해줘’, ‘갈피에서 로봇 검색해줘’, ‘사이트에서 프로젝트 눌러줘’처럼 말해 줘. 번호로는 ‘/웹 클릭 w3’도 돼요.";
        }
        catch (Exception error) when (error is PlaywrightException or TimeoutException)
        {
            if (context.Browser is not null) context.Browser.Used = true;
            throw new ApiFailure(502, "browser_failed", "사이트 동작을 끝까지 확인하지 못했어요. 같은 클릭을 반복하지 않았어요. ‘사이트 화면 보여줘’로 현재 상태를 확인해 줘.");
        }
    }
    private static bool SearchField(BrowserElement e) => e.Kind == "input" && (e.InputType == "search" || Regex.IsMatch(e.Label, "검색|search", RegexOptions.IgnoreCase)) && e.InputType is "search" or "text";
    private static string Compact(string lead, string observed)
    {
        var header = string.Join("\n", observed.Split('\n').Take(2));
        var parts = observed.Split("화면 글 일부:\n", 2);
        var excerpt = parts.Length == 2 ? Regex.Replace(parts[1], @"\s+", " ").Trim() : "";
        if (excerpt.Length > 260) excerpt = excerpt[..260] + "…";
        return lead + "\n" + header + "\n\n" + excerpt + "\n\n메뉴 이름을 말하거나 ‘사이트 화면 보여줘’로 번호를 확인해 줘.";
    }
    public static bool NavigationControl(string kind, string label, bool inForm) =>
        !Regex.IsMatch(label, "삭제|지우기|등록|추가|저장|전송|보내|결제|구매|주문|신청|로그인|로그아웃|설치|허용|복원|동의|수락|구독|공유|업로드|다운로드|delete|remove|save|send|submit|pay|purchase|order|sign.?in|log.?in|log.?out|install|allow|accept|subscribe|share|upload|download", RegexOptions.IgnoreCase) &&
        (kind == "link" || kind == "button" && !inForm && Regex.IsMatch(label, "보기|열기|펼치기|접기|닫기|메뉴|다음|이전|검색|탐색|필터|정렬|^(?:이용 안내|상담 시작|내 상담기록)$|view|open|menu|next|previous|search|filter|sort", RegexOptions.IgnoreCase));
    private static BrowserElement Select(string target, SkillContext context)
    {
        var view = context.Browser!;
        var key = target.Trim().Trim('‘', '’', '"');
        if (Regex.IsMatch(key, @"^w?\d+(?:번)?$"))
        { int number = int.Parse(Regex.Replace(key, @"\D", "")); return view.Elements.FirstOrDefault(e => e.Number == number) ?? throw new ApiFailure(400, "browser_target", "현재 화면에 그 번호가 없어요."); }
        var exact = view.Elements.Where(e => e.Label == key).ToArray();
        var found = exact.Length > 0 ? exact : view.Elements.Where(e => e.Label.Contains(key, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (found.Length != 1) throw new ApiFailure(400, "browser_ambiguous", "같은 이름이 여러 개이거나 찾지 못했어요. ‘사이트 화면 보여줘’에서 w번호를 골라 줘.");
        return found[0];
    }
    private static void Fresh(SkillContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var view = context.Browser!;
        if (view.Used || DateTimeOffset.UtcNow - view.ObservedAt > TimeSpan.FromMinutes(2) || view.Page.Url != view.Url)
            throw new ApiFailure(409, "browser_stale", "사이트 화면이 바뀌었거나 오래됐어요. ‘사이트 화면 보여줘’로 다시 읽어 줘.");
    }
    private async Task Fill(BrowserElement element, string value, SkillContext context, CancellationToken token)
    {
        Fresh(context, token);
        if (!SearchField(element) || value.Length is < 1 or > 200) throw new ApiFailure(400, "browser_input", "현재는 검색칸에 200자 이내로 입력할 수 있어요.");
        if (!await element.Handle.IsVisibleAsync()) throw new ApiFailure(409, "browser_stale", "입력칸이 바뀌었어요. 화면을 다시 읽어 줘.");
        context.Browser!.Used = true;
        await element.Handle.FillAsync(value);
        if (await element.Handle.InputValueAsync() != value) throw new ApiFailure(502, "browser_verify", "입력 결과가 일치하지 않아요. 자동으로 다시 입력하지 않았어요.");
    }
    private async Task Click(BrowserElement element, SkillContext context, CancellationToken token)
    {
        Fresh(context, token);
        var live = await element.Handle.EvaluateAsync<JsonElement>("e => ({name:(e.getAttribute('aria-label') || e.innerText || '').trim().replace(/\\s+/g,' '),form:!!e.closest('form')})");
        if (!NavigationControl(element.Kind, element.Label, element.InForm) ||
            !NavigationControl(element.Kind, live.GetProperty("name").GetString() ?? "", live.GetProperty("form").GetBoolean()))
            throw new ApiFailure(400, "browser_action", "이 항목은 아직 조작 범위에 없어요. 현재는 자료·메뉴 열기와 검색을 지원해요.");
        if (element.Kind == "link")
        {
            string currentHref = await element.Handle.EvaluateAsync<string>("e => e.href || ''");
            if (currentHref != element.Href || !Allowed(currentHref)) throw new ApiFailure(400, "browser_link", "등록되지 않았거나 주소가 바뀐 링크예요. 열 사이트를 먼저 등록해 줘.");
        }
        if (!await element.Handle.IsVisibleAsync() || !await element.Handle.IsEnabledAsync()) throw new ApiFailure(409, "browser_stale", "항목이 바뀌었어요. 사이트 화면을 다시 읽어 줘.");
        context.Browser!.Used = true;
        var original = context.Browser.Page; var before = browser!.Pages.ToHashSet();
        IPage? popup = null;
        if (element.Kind == "link" && await element.Handle.GetAttributeAsync("target") == "_blank")
            popup = await original.RunAndWaitForPopupAsync(() => element.Handle.ClickAsync(), new() { Timeout = 10000 });
        else { await element.Handle.ClickAsync(); popup = browser.Pages.LastOrDefault(p => !before.Contains(p) && !p.IsClosed); }
        var page = popup ?? original;
        try { await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new() { Timeout = 10000 }); } catch (TimeoutException) { }
        if (!Allowed(page.Url)) throw new ApiFailure(400, "browser_link", "등록된 사이트 밖으로 이동하려 해서 중단했어요.");
        await Observe(page, context);
    }
    private async Task<string> Observe(IPage page, SkillContext context)
    {
        if (!Allowed(page.Url)) throw new ApiFailure(400, "browser_link", "등록된 사이트에서만 화면을 읽을 수 있어요.");
        var elements = new List<BrowserElement>();
        foreach (var handle in await page.QuerySelectorAllAsync("a[href],button,input,[role=button],[role=link]"))
        {
            if (elements.Count >= 60) break;
            if (!await handle.IsVisibleAsync()) continue;
            var data = await handle.EvaluateAsync<JsonElement>("""
                e => { const tag=e.tagName.toLowerCase(), heading=e.closest('article')?.querySelector('h2,h3')?.textContent?.trim();
                const name=(e.getAttribute('aria-label') || e.labels?.[0]?.textContent || e.innerText || e.getAttribute('placeholder') || '').trim().replace(/\s+/g,' ').slice(0,110);
                return {label:(heading && !name.includes(heading) ? heading+' · ' : '')+name,kind:tag==='a'?'link':tag==='input'?'input':'button',href:e.href||'',type:e.type||'',form:!!e.closest('form')}; }
                """);
            string label = data.GetProperty("label").GetString() ?? ""; if (label.Length == 0) continue;
            elements.Add(new(elements.Count + 1, label, data.GetProperty("kind").GetString()!, data.GetProperty("href").GetString()!, data.GetProperty("type").GetString()!, data.GetProperty("form").GetBoolean(), handle));
        }
        var previous = context.Browser;
        context.Browser = new(page, page.Url, DateTimeOffset.UtcNow, elements.ToArray());
        if (previous is not null) { previous.Used = true; foreach (var item in previous.Elements) try { await item.Handle.DisposeAsync(); } catch (PlaywrightException) { } }
        string excerpt = await page.EvaluateAsync<string>("() => (document.querySelector('#library-results') || document.querySelector('main') || document.body).innerText.slice(0,900)");
        return $"{await page.TitleAsync()}\n{page.Url}\n" + string.Join("\n", elements.Select(e => $"w{e.Number} · {e.Kind} · {e.Label}")) + "\n\n화면 글 일부:\n" + excerpt;
    }
    public async ValueTask DisposeAsync() { if (browser is not null) try { await browser.CloseAsync(); } catch (PlaywrightException) { } playwright?.Dispose(); }
}
