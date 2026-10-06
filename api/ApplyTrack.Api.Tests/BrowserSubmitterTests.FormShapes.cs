// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using ApplyTrack.Api.Agent;
using ApplyTrack.Api.Agent.Browser;
using ApplyTrack.Api.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace ApplyTrack.Api.Tests;

/// <summary>
/// Form shapes met on the 2026-10-06 Ready rows (#431–#439): Greenhouse's checkbox groups and
/// react-select menus (Fieldwire, Stripe), its Education pickers, a gone-notice drawn late
/// (Torentify), a form inside a shadow root (Manatal), a notice over the form and a Filestack
/// picker (Flexhire), an international phone box and a résumé parse that rewrites fields
/// (Motion Recruitment, Robert Half), and boxes whose placeholder is only "Enter" (Fullstack).
/// </summary>
public sealed partial class BrowserSubmitterTests
{
    private static void MapFormShapeFixtures(WebApplication app)
    {
        app.MapGet("/jobs/gh-shapes", () => Results.Content(GreenhouseShapesHtml, "text/html"));
        app.MapGet("/jobs/gh-education", () => Results.Content(GreenhouseEducationHtml, "text/html"));
        app.MapGet("/jobs/late-gone", () => Results.Content(LateGoneHtml, "text/html"));
        app.MapGet("/jobs/shadow-form", () => Results.Content(ShadowFormHtml, "text/html"));
        app.MapGet("/jobs/notice-picker", () => Results.Content(NoticePickerHtml, "text/html"));
        app.MapGet("/jobs/phone-widget", () => Results.Content(PhoneWidgetHtml, "text/html"));
        app.MapGet("/jobs/enter-placeholders", () => Results.Content(EnterPlaceholdersHtml, "text/html"));
    }

    // A react-select stand-in: an <input role=combobox> whose menu exists only while open and
    // filters on what is typed, a hidden required input holding the committed value, and the
    // value posted under the question's name. "multi" keeps several (value container --is-multi).
    private const string ComboScript = """
        <script>
          function combo(id, options, multi) {
            const input = document.getElementById(id);
            const menu = document.getElementById('react-select-' + id + '-listbox');
            const shown = document.getElementById(id + '-value');
            const hidden = input.closest('.select__container').querySelector('input[data-for]');
            const post = document.getElementById(id + '-post');
            const picked = [];
            const render = () => {
              const q = input.value.trim().toLowerCase();
              menu.innerHTML = '';
              menu.style.display = 'block';
              input.setAttribute('aria-controls', menu.id);
              const hits = options.filter(o => o.toLowerCase().includes(q));
              if (hits.length === 0) { menu.innerHTML = '<div class="no-options">No options</div>'; return; }
              for (const o of hits) {
                const d = document.createElement('div');
                d.setAttribute('role', 'option'); d.textContent = o;
                d.onmousedown = e => { e.preventDefault(); choose(o); };
                menu.appendChild(d);
              }
            };
            const choose = o => {
              if (multi) { if (!picked.includes(o)) picked.push(o); } else { picked.length = 0; picked.push(o); }
              shown.innerHTML = picked.map(p => '<span class="' + (multi ? 'select__multi-value' : 'select__single-value') + '">' + p + '</span>').join('');
              hidden.value = picked.join(',');
              post.value = picked.join(',');
              input.value = '';
              menu.style.display = 'none';
              input.removeAttribute('aria-controls');
            };
            input.addEventListener('focus', render);
            input.addEventListener('click', render);
            input.addEventListener('input', render);
            input.addEventListener('keydown', e => { if (e.key === 'Escape') { menu.style.display = 'none'; input.removeAttribute('aria-controls'); } });
            input.addEventListener('blur', () => setTimeout(() => { menu.style.display = 'none'; }, 150));
          }
        </script>
        """;

    private static string Combo(string id, string label, bool multi = false) => $"""
        <div class="select__container">
          <label id="{id}-label" for="{id}">{label}<span aria-hidden="true">*</span></label>
          <div class="select-shell">
            <div class="select__value-container{(multi ? " select__value-container--is-multi" : "")}">
              <div id="{id}-value"></div>
              <input id="{id}" type="text" role="combobox" aria-autocomplete="list" aria-haspopup="true" aria-required="true" aria-labelledby="{id}-label" autocomplete="off" value="">
            </div>
            <input required tabindex="-1" aria-hidden="true" data-for="{id}" style="opacity:0;width:1px;height:1px" value="">
          </div>
          <div id="react-select-{id}-listbox" role="listbox" style="display:none"></div>
          <input type="hidden" name="{id}" id="{id}-post" value="">
        </div>
        """;

    private static readonly string GreenhouseShapesHtml = $$"""
        <html><body>
        {{ComboScript}}
        <form method="post" action="/apply" enctype="multipart/form-data">
          <label for="first_name">First Name</label><input id="first_name" name="first_name" required />
          {{Combo("question_7", "Are you legally authorized to work in the U.S. without any restrictions?")}}
          {{Combo("question_8[]", "Do you have experience coding with any of the languages listed below?", multi: true)}}
          {{Combo("question_9", "Please select the country where you currently reside.")}}
          <div class="field-wrapper"><fieldset class="checkbox" id="question_10[]" aria-required="true">
            <legend>Do you have professional working experience with cloud infrastructure tools? <span class="required">*</span></legend>
            <div><input type="checkbox" id="question_10[]_1" name="question_10[]" value="1"><label for="question_10[]_1">AWS</label></div>
            <div><input type="checkbox" id="question_10[]_2" name="question_10[]" value="2"><label for="question_10[]_2">GCP</label></div>
            <div><input type="checkbox" id="question_10[]_3" name="question_10[]" value="3"><label for="question_10[]_3">Azure</label></div>
          </fieldset></div>
          <button type="submit">Submit application</button>
        </form>
        <script>
          combo('question_7', ['Yes', 'No']);
          combo('question_8[]', ['Ruby on Rails', 'Rust', 'Both', 'Neither'], true);
          combo('question_9', ['Australia', 'Canada', 'UK', 'US', 'Other']);
        </script>
        </body></html>
        """;

    private static readonly string GreenhouseEducationHtml = $$"""
        <html><body>
        {{ComboScript}}
        <form method="post" action="/apply" enctype="multipart/form-data">
          <label for="first_name">First Name</label><input id="first_name" name="first_name" required />
          <h3>Education</h3>
          {{Combo("school--0", "School")}}
          {{Combo("degree--0", "Degree")}}
          <button type="submit">Submit application</button>
        </form>
        <script>
          combo('school--0', ['University of Maryland Global Campus', 'University of Maryland - University College', 'University of Michigan']);
          combo('degree--0', ["Associate's Degree", "Bachelor's Degree", "Master's Degree", 'Other']);
        </script>
        </body></html>
        """;

    // Torentify: the posting page renders, then its script asks for the job and draws the
    // gone-notice — after the run's first look. Only the site's own search box is on it.
    private const string LateGoneHtml = """
        <html><body>
        <nav><form role="search" action="/search"><input name="q" placeholder="Enter job title..." /></form></nav>
        <main id="main"><p>Loading…</p></main>
        <script>
          setTimeout(() => {
            document.getElementById('main').innerHTML = '<h2>Job Not Found</h2><p>The job you are looking for could not be found. Either it has expired, or the link is invalid.</p>';
          }, 7000);
        </script>
        </body></html>
        """;

    // Manatal: the whole application lives in a web component's open shadow root; labels carry
    // the only required mark, a star.
    private const string ShadowFormHtml = """
        <html><body>
        <h1>Software Developer</h1>
        <div id="host"></div>
        <script>
          const root = document.getElementById('host').attachShadow({ mode: 'open' });
          root.innerHTML = `
            <form id="f" method="post" action="/apply" enctype="multipart/form-data">
              <div><label for="full_name"><p>What is your full name?<span>*</span></p></label><input id="full_name" placeholder="Your full name*"></div>
              <div><label for="e1"><p>What is your email address?<span>*</span></p></label><input id="e1" name="email" placeholder="olivia@manatal.com"></div>
              <div><label for="position"><p>What is your current position?<span>*</span></p></label><input id="position"></div>
              <div><label><p>Resume<span>*</span></p></label><input type="file" name="resume" style="display:none" id="file1">
                <input id="att" placeholder="Select the attachment" readonly><button type="button" onclick="this.getRootNode().getElementById('file1').click()">Browse</button></div>
              <button type="submit">Apply now</button>
            </form>`;
          root.getElementById('file1').addEventListener('change', e => { root.getElementById('att').value = e.target.files[0]?.name || ''; });
        </script>
        </body></html>
        """;

    // Flexhire: Apply opens the form under a notice ("we couldn't detect your location") whose
    // only button is Ok and whose one link is set in its sentence; "Upload Resume/CV" opens a
    // picker that makes its own file input and sends nothing until its Upload is pressed.
    private const string NoticePickerHtml = """
        <html><body>
        <form method="post" action="/apply" enctype="multipart/form-data">
          <label for="fn">First Name *</label><input id="fn" name="first_name" required />
          <label for="ln">Last Name *</label><input id="ln" name="last_name" required />
          <label for="em">Email *</label><input id="em" name="email" type="email" required />
          <p>Resume/CV *</p>
          <button type="button" id="up">Upload Resume/CV</button>
          <span id="uploaded"></span>
          <input type="hidden" name="resume_name" id="resume_name">
          <button type="submit">Submit Application</button>
        </form>
        <div id="backdrop" style="position:fixed;inset:0;background:rgba(0,0,0,.4);z-index:10">
          <div role="dialog" aria-modal="true" style="background:#fff;margin:100px auto;width:400px;padding:20px">
            <h2>Apply to Job</h2>
            <p>This job accepts applicants located in Argentina, Bolivia, Brazil <button type="button">and 21 more countries</button>. We couldn't detect your location, set it below to see if you are eligible to apply.</p>
            <button type="button" onclick="document.getElementById('backdrop').remove()">Ok</button>
          </div>
        </div>
        <script>
          document.getElementById('up').onclick = () => {
            const picker = document.createElement('div');
            picker.className = 'fsp-picker';
            picker.innerHTML = '<input id="fsp-fileUpload" type="file"><span role="button" class="fsp-button-upload"><span>Upload <span style="display:none">1</span></span></span>';
            document.body.appendChild(picker);
            picker.querySelector('[role=button]').onclick = () => {
              const f = picker.querySelector('input').files[0];
              if (!f) return;
              document.getElementById('uploaded').textContent = f.name;
              document.getElementById('resume_name').value = f.name;
              picker.remove();
            };
          };
        </script>
        </body></html>
        """;

    // Motion Recruitment's react-international-phone: the box opens on "+1 " and resets itself
    // when its value loses the dial code — a fill() wipes it. Robert Half's résumé parse: the
    // upload rewrites the name and phone the run had typed.
    private const string PhoneWidgetHtml = """
        <html><body>
        <form method="post" action="/apply" enctype="multipart/form-data">
          <label for="firstName">First Name: *</label><input id="firstName" name="firstName" required />
          <label for="phone-input-id">Phone Number: *</label><input id="phone-input-id" name="phone" type="tel" value="+1 " required />
          <label for="resume">Upload Your Resume: *</label><input id="resume" name="resume" type="file" />
          <button type="submit">Submit</button>
        </form>
        <script>
          const phone = document.getElementById('phone-input-id');
          phone.addEventListener('input', () => { if (!phone.value.startsWith('+1')) phone.value = '+1 '; });
          document.getElementById('resume').addEventListener('change', () => {
            fetch('/jobs/blank').then(() => { document.getElementById('firstName').value = 'PARSED'; });
          });
        </script>
        </body></html>
        """;

    // Fullstack's talent portal: every box's placeholder is "Enter"; the field's title is the
    // text above it, with its star.
    private const string EnterPlaceholdersHtml = """
        <html><body>
        <form method="post" action="/apply">
          <div class="field"><div class="title">First Name<span>*</span></div><div><input name="talent.firstName" placeholder="Enter"></div></div>
          <div class="field"><div class="title">Phone<span>*</span></div><div><input name="talent.phone" placeholder="Enter"></div></div>
          <div class="field"><div class="title">Portfolio URL</div><div><input name="talent.portfolio" placeholder="Enter"></div></div>
          <button type="submit">Submit</button>
        </form>
        </body></html>
        """;

    [SkippableFact]
    public async Task A_checkbox_group_and_react_select_menus_are_discovered_whole()
    {
        // Fieldwire / Stripe: each box of a "select all that apply" was its own Yes/No question,
        // the group's id labelled by its first option (#431); react-select questions came back as
        // free text with no options, and the drafter wrote prose to them (#432).
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var questions = await Discoverer().DiscoverAsync($"{_fixtureUrl}/jobs/gh-shapes");
        Assert.NotNull(questions);
        var cloud = Assert.Single(questions!, q => q.Id == "question_10[]");
        Assert.Equal(PacketQuestion.MultiSelect, cloud.Type);
        Assert.Equal(["AWS", "GCP", "Azure"], cloud.Options);
        Assert.Equal("Do you have professional working experience with cloud infrastructure tools?", cloud.Label);
        Assert.True(cloud.Required);
        Assert.DoesNotContain(questions!, q => q.Id is "GCP" or "Azure");
        var auth = Assert.Single(questions!, q => q.Id == "question_7");
        Assert.Equal(PacketQuestion.Select, auth.Type);
        Assert.Equal(["Yes", "No"], auth.Options);
        var langs = Assert.Single(questions!, q => q.Id == "question_8[]");
        Assert.Equal(PacketQuestion.MultiSelect, langs.Type);
        Assert.Equal(["Ruby on Rails", "Rust", "Both", "Neither"], langs.Options);
    }

    [SkippableFact]
    public async Task A_packet_that_split_a_checkbox_group_and_wrote_prose_to_menus_still_fills_the_form()
    {
        // The packets already in Ready: per-box Yes/No questions, and drafted prose for menus
        // the drafter never saw. "No" boxes must stay unticked; the prose picks what it means.
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var packet = new AgentPacket
        {
            ApplicationName = "fieldwire-backend.md", Provider = "greenhouse",
            Questions =
            [
                new("first_name", "First Name", true, PacketQuestion.Text, [], PacketQuestion.Standard),
                new("question_7", "Are you legally authorized to work in the U.S. without any restrictions?", true, PacketQuestion.Text, [], PacketQuestion.Custom),
                new("question_8[]", "Do you have experience coding with any of the languages listed below?", true, PacketQuestion.Text, [], PacketQuestion.Custom),
                new("question_9", "Please select the country where you currently reside.", true, PacketQuestion.Text, [], PacketQuestion.Custom),
                new("question_10[]", "AWS", true, PacketQuestion.Select, ["Yes", "No"], PacketQuestion.Custom),
                new("GCP", "GCP", true, PacketQuestion.Select, ["Yes", "No"], PacketQuestion.Custom),
                new("Azure", "Azure", true, PacketQuestion.Select, ["Yes", "No"], PacketQuestion.Custom),
            ],
            Answers = new()
            {
                ["first_name"] = "Ada",
                ["question_7"] = "US Citizen",
                ["question_8[]"] = "Yes — C# and Python are my deepest languages, plus Rust (a hexagonal core). No Ruby on Rails experience.",
                ["question_9"] = "United States of America",
                ["question_10[]"] = "Yes", ["GCP"] = "No", ["Azure"] = "Yes",
            },
        };
        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/gh-shapes", packet, null, dryRun: false);
        Assert.True(outcome.Submitted, outcome.Error + " unmapped: " + string.Join(", ", outcome.Unmapped));
        var post = Assert.Single(_posts);
        Assert.Equal("Yes", post["question_7"]);
        Assert.Equal("Rust", post["question_8[]"]);
        Assert.Equal("US", post["question_9"]);
        Assert.Equal("1,3", post["question_10[]"]);
    }

    [SkippableFact]
    public async Task Education_the_resume_filled_is_not_left_listed_unmapped()
    {
        // Stripe: the packet's "University of Maryland University College" is no option, the
        // résumé's school then filled the picker — and the run still listed it unmapped (#433).
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var packet = new AgentPacket
        {
            ApplicationName = "stripe-backend.md", Provider = "greenhouse",
            Questions =
            [
                new("first_name", "First Name", true, PacketQuestion.Text, [], PacketQuestion.Standard),
                new("school--0", "School", true, PacketQuestion.Text, [], PacketQuestion.Custom),
                new("degree--0", "Degree", true, PacketQuestion.Text, [], PacketQuestion.Custom),
            ],
            Answers = new()
            {
                ["first_name"] = "Ada",
                ["school--0"] = "University of Maryland University College",
                ["degree--0"] = "Bachelor of Science, Computer and Information Science",
            },
            Resume = new Resume { Education = [new("University of Maryland University College", "Bachelor of Science", "Computer and Information Science", "2010 - 2014")] },
        };
        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/gh-education", packet, null, dryRun: true);
        Assert.True(outcome.Filled, outcome.Error);
        Assert.Empty(outcome.Unmapped);
        Assert.Contains("school--0", outcome.Mapped);
        Assert.Contains("degree--0", outcome.Mapped);
    }

    [SkippableFact]
    public async Task A_gone_notice_drawn_after_the_first_look_retires_the_posting()
    {
        // Torentify: "Job Not Found" arrived after the closed-posting check, and the run said
        // "nothing on this form could be filled" about the site's search box (#435).
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/late-gone", StandardPacket(), (Pdf, "resume.pdf"), dryRun: true);
        Assert.True(outcome.Closed, outcome.Error);
        Assert.Empty(_posts);
    }

    [Fact]
    public void Gone_notices_and_ashby_embeds_are_read_from_their_words()
    {
        Assert.True(BrowserSubmitter.IsClosedPosting("Oops! We couldn’t find that page. The page might have been relocated."));   // Fullstack (#435)
        Assert.True(BrowserSubmitter.IsClosedPosting("Job Not Found The job you are looking for could not be found."));
        Assert.False(BrowserSubmitter.IsClosedPosting("We couldn't find a better team to join."));
        // Hercules: ?ashby_jid= on the employer's own careers page (#434).
        const string link = "https://hercules.app/careers?ashby_jid=9ee9a087-9c1b-40e3-ab15-f28d42802314&utm_source=x";
        Assert.True(AtsProvider.HasAshbyJobId(link));
        Assert.Equal("https://jobs.ashbyhq.com/hercules/9ee9a087-9c1b-40e3-ab15-f28d42802314/application", AtsProvider.AshbyHostedForm(link));
        Assert.Equal("https://jobs.ashbyhq.com/acme-labs/9ee9a087-9c1b-40e3-ab15-f28d42802314/application",
            AtsProvider.AshbyHostedForm("https://careers.acme.io/?ashby_jid=9ee9a087-9c1b-40e3-ab15-f28d42802314",
                "<script src=\"https://jobs.ashbyhq.com/acme-labs/embed?version=2\"></script>"));
        Assert.Null(AtsProvider.AshbyHostedForm("https://jobs.ashbyhq.com/hercules/9ee9a087-9c1b-40e3-ab15-f28d42802314"));
        Assert.Null(AtsProvider.AshbyHostedForm("https://hercules.app/careers"));
    }

    [Fact]
    public void A_menu_choice_is_what_the_answer_means()
    {
        // #432: prose drafted to a menu the drafter never saw.
        Assert.Equal(["Yes"], BrowserSubmitter.MenuChoices(["Yes", "No"], "Yes. At Ronin 48 I build AI features.", false));
        Assert.Equal(["No"], BrowserSubmitter.MenuChoices(["Yes", "No"], "No, I do not.", false));
        Assert.Equal(["Yes"], BrowserSubmitter.MenuChoices(["Yes", "No"], "US Citizen", false, "Are you legally authorized to work in the U.S.?"));
        Assert.Empty(BrowserSubmitter.MenuChoices(["Yes", "No"], "US Citizen", false, "Will you require sponsorship?"));
        Assert.Empty(BrowserSubmitter.MenuChoices(["Yes", "No"], "Not without sponsorship", false));
        Assert.Equal(["US"], BrowserSubmitter.MenuChoices(["Australia", "UK", "US", "Other"], "United States of America", false));
        Assert.Equal(["Rust"], BrowserSubmitter.MenuChoices(["Ruby on Rails", "Rust", "Both", "Neither"],
            "C# and Python, plus Rust. No Ruby on Rails experience.", true));
        Assert.Equal(["AWS", "Azure"], BrowserSubmitter.MenuChoices(["AWS", "GCP", "Azure"], "AWS, Azure", true));
        Assert.Empty(BrowserSubmitter.MenuChoices(["Seattle", "New York"], "I live in Nebraska.", false));
    }

    [SkippableFact]
    public async Task A_form_inside_a_shadow_root_is_discovered_and_its_resume_seen_to_take()
    {
        // Manatal (yo-ai-labs): discovery found nothing, and the attached résumé was never seen,
        // "required fields could not be mapped (std:resume)" (#436).
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var questions = await Discoverer().DiscoverAsync($"{_fixtureUrl}/jobs/shadow-form");
        Assert.NotNull(questions);
        Assert.Contains(questions!, q => q.Id == "full_name" && q.Label == "What is your full name?" && q.Required);
        Assert.Contains(questions!, q => q.Id == "position" && q.Required);

        var packet = StandardPacket();
        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/shadow-form", packet, (Pdf, "resume.pdf"), dryRun: true);
        Assert.Contains("std:resume", outcome.Mapped);
        Assert.DoesNotContain("std:resume", outcome.Unmapped);
        // The starred position box the packet never knew is named, and handed to the drafter.
        Assert.Contains("position", outcome.Unmapped);
        Assert.Contains(outcome.Discovered ?? [], q => q.Id == "position");
    }

    [SkippableFact]
    public async Task A_notice_over_the_form_is_acknowledged_and_a_picker_upload_is_finished()
    {
        // Flexhire: the Ok-only notice took the résumé button's click, and the Filestack picker
        // made a file input and waited for its own Upload — "Resume is required" (#437).
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/notice-picker", StandardPacket(), (Pdf, "resume.pdf"), dryRun: false);
        Assert.True(outcome.Submitted, outcome.Error + " unmapped: " + string.Join(", ", outcome.Unmapped));
        var post = Assert.Single(_posts);
        Assert.Equal("resume.pdf", post["resume_name"]);
        Assert.Equal("Ada", post["first_name"]);
    }

    [SkippableFact]
    public async Task A_phone_box_holding_a_dial_code_is_typed_after_it_and_a_resume_parse_does_not_win()
    {
        // Motion Recruitment: fill() into "+1 " left "+1 " (#438). Robert Half: the résumé went up
        // after the typing and its parse rewrote what had been typed (#438).
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var packet = new AgentPacket
        {
            ApplicationName = "motion-dotnet.md", Provider = "unknown",
            Questions =
            [
                new("firstName", "First Name:", true, PacketQuestion.Text, [], PacketQuestion.Standard),
                new("phone", "Phone Number:", true, PacketQuestion.Text, [], PacketQuestion.Standard),
                new("resume", "Upload Your Resume:", true, PacketQuestion.File, [], PacketQuestion.Standard),
            ],
            Answers = new() { ["firstName"] = "Ada", ["phone"] = "5712975406" },
        };
        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/phone-widget", packet, (Pdf, "resume.pdf"), dryRun: false);
        Assert.True(outcome.Submitted, outcome.Error);
        var post = Assert.Single(_posts);
        Assert.Equal("+1 5712975406", post["phone"]);
        Assert.Equal("Ada", post["firstName"]);
    }

    [Fact]
    public void A_dial_code_prefix_and_the_national_number_are_read_apart()
    {
        Assert.Equal("1", BrowserSubmitter.DialCodePrefix("+1 "));
        Assert.Equal("44", BrowserSubmitter.DialCodePrefix(" +44"));
        Assert.Null(BrowserSubmitter.DialCodePrefix(""));
        Assert.Null(BrowserSubmitter.DialCodePrefix("+1 (571) 297-5406"));
        Assert.Equal("5712975406", BrowserSubmitter.NationalDigits("5712975406", "1"));
        Assert.Equal("5712975406", BrowserSubmitter.NationalDigits("+1 571 297 5406", "1"));
        Assert.Equal("5712975406", BrowserSubmitter.NationalDigits("15712975406", "1"));
        Assert.Equal("1234567890", BrowserSubmitter.NationalDigits("1234567890", "1"));
    }

    [SkippableFact]
    public async Task A_placeholder_that_only_says_enter_does_not_name_the_field()
    {
        // Fullstack: every question came back "Enter", optional (#439).
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var questions = await Discoverer().DiscoverAsync($"{_fixtureUrl}/jobs/enter-placeholders");
        Assert.NotNull(questions);
        var phone = Assert.Single(questions!, q => q.Id == "talent.phone");
        Assert.Equal("Phone", phone.Label);
        Assert.True(phone.Required);
        var portfolio = Assert.Single(questions!, q => q.Id == "talent.portfolio");
        Assert.Equal("Portfolio URL", portfolio.Label);
        Assert.False(portfolio.Required);
    }
}
