# Accessibility

OSApplyTrack targets the Web Content Accessibility Guidelines (WCAG) 2.2 at Level AA. Accessibility is treated as an ongoing engineering requirement, not a one-time certification.

## Supported behavior

- Complete keyboard operation without application-specific shortcuts.
- Semantic landmarks, headings, forms, lists, tabs, dialogs, and status announcements for screen readers.
- System, light, and dark color modes; system or high contrast; system or reduced motion; three text sizes; and comfortable or compact spacing.
- Reflow at 400% browser zoom and layouts that remain usable with the app's 150% text setting.
- System preference detection before sign-in. Explicit choices are stored only in the current browser.
- Visible focus, 44px minimum targets, text labels for statuses, and no information conveyed by color alone.
- Mobile touch and gesture scroll containment (`overscroll-behavior: contain`) so swiping within the application list remains contained without triggering page rubber-banding or browser pull-to-refresh gestures.
- Modal dialogs and drawers (including Settings and the Pipeline view) implement standard ARIA modal semantics (`role="dialog"`, `aria-labelledby`, focus management and trapping, backdrop dismissal, and Escape key dismissal).
- Flexible, accessible form controls: explicitly sized checkbox targets (e.g. the sign-in Terms of Service checkbox and bulk Ready-lane selectors) paired with responsive text spans that prevent word-per-line wrapping distortion and ensure reliable touch interaction.

## Verification matrix

Automated Playwright and axe-core checks cover login, the application list and detail view, editing, validation, every settings section, the Pipeline queue view modal, bulk lane selection, responsive navigation, and preference modes on every pull request.

Before a release that materially changes the interface, manually verify current versions of:

| Platform | Browser | Assistive technology |
| --- | --- | --- |
| Windows | Firefox and Chrome | NVDA |
| macOS | Safari | VoiceOver |
| iOS | Safari | VoiceOver |
| Windows | Edge | Forced colors / High Contrast |

Keyboard-only testing is required on both desktop and mobile-width layouts. Automated results cannot establish conformance by themselves.

## Known limitations

- The interface is currently available only in English.
- Display and sensory preferences are browser-local and do not synchronize between devices.
- Markdown notes may include structures supplied by job postings. They are sanitized before display, but unusually authored tables or code blocks may require horizontal scrolling within that content block.

## Report a problem

Use the repository's **Accessibility problem** issue template. Include the affected workflow, browser, assistive technology when applicable, and the behavior you expected. Do not attach application data, résumé content, email addresses, API keys, or other private information.
