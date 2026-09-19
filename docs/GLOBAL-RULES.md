# Owner-Mandated Global Rules — قوانین سراسری اجباری مالک

> **Binding on every change, every fix, every added section/feature/capability, every
> request, every prompt, and every scheduled run.** Auto-applied when embedded in a
> project's `CLAUDE.md`.
>
> در هر تغییر، هر اصلاح، هر بخش/ویژگی اضافه‌شده، هر درخواست، هر پرامپت و هر اجرای
> زمان‌بندی‌شده الزامی است.

Owner: Mohammad.

---

## The rules — قوانین

### R1 — Ripple review (dependency impact) — بررسی اثر زنجیره‌ای
After any change/fix/addition, find and fix everything that depends on it.
**Done when:** every reference to the changed symbol / route / DTO / config key is updated;
callers, DI registrations, EF migrations, Razor & RCL views, JS/TS, API clients, and tests
are consistent; the solution builds; affected tests pass.
هر تغییر، در هر جای دیگری که از آن استفاده می‌شود نیز یافته و اصلاح شود.

### R2 — Complete code only — فقط کد کامل
No partial, stubbed, or half-written code; never under-deliver a task, intentionally or
not.
**Done when:** no `TODO` / `NotImplementedException` / placeholder / dead code; every path
handled (success, empty, error, cancellation); nullability and async/await correct.
کد کامل؛ بدون بخش ناتمام؛ همهٔ مسیرها مدیریت‌شده.

### R3 — Research first, then optimize — تحقیق، سپس بهینه‌سازی
Study similar patterns already in the repo and reuse them. If a capability is missing or
can be made more complete or optimal, implement it — not the bare minimum.
**Done when:** the change matches existing conventions; no needless new pattern; obvious
in-scope gaps and easy wins are handled.
ابتدا نمونه‌های مشابه بررسی، سپس کامل‌ترین راه‌حل.

### R4 — Best UI/UX — بهترین UI/UX
Every surface is attractive, consistent, accessible, and easy to use.
**Done when:** matches the design system/theme; keyboard- and screen-reader-usable;
loading/empty/error/success states exist; responsive with no layout shift; validation is
clear.
هر صفحه جذاب، سازگار، در دسترس و آسان.

### R5 — Honest reporting — گزارش صادقانه
No exaggeration. State only what was actually done and verified; list blockers plainly.
**Done when:** the report matches the real diff and verification results; gated items
marked as gated, never claimed done.
گزارش دقیقاً مطابق کار انجام‌شده و بدون اغراق.

### R6 — No demo/mock/sample — بدون دمو/نمونه
Nothing left as demo, sample, mock, placeholder, or temporary hack; everything operational
against real data and services.
**Done when:** no fake success, no seed-only path standing in for real logic; real
persistence/integration wired and working.
همه‌چیز عملیاتی و واقعی؛ بدون دمو یا نمونه.

### R7 — Multilingual + bidirectional (i18n + RTL/LTR) — چندزبانه و دوجهته
All code and all pages support multiple languages and both RTL and LTR.
**Concretely:** no hardcoded user-facing strings — use the stack's localization
(`IStringLocalizer`/`.resx` in ASP.NET Core); enable request localization with the
supported cultures; set `<html lang dir>` from the current culture; use CSS logical
properties (`margin-inline`, `inset-inline`, `text-align:start/end`) instead of left/right;
mirror directional icons; format numbers/dates per culture; add every new key to every
resource file.
**Done when:** no literal user-facing string remains in code; the page renders correctly in
both RTL and LTR; no left/right hardcoding remains.
بدون رشتهٔ ثابت؛ منابع محلی‌سازی؛ RTL و LTR هر دو درست.

---

## Per-change checklist — چک‌لیست هر تغییر
1. Understand the area; read similar existing code first (R3).
2. Implement fully (R2, R6); build UI to the design system (R4); localize it (R7).
3. Ripple: search all references; update callers, DI, migrations, views, tests (R1).
4. Build; run the affected tests; smoke-test if a host or route changed.
5. Update trackers; report honestly (R5).
