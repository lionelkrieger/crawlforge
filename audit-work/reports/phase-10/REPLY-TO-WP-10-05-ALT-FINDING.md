# Coordinator reply to cf-wp-10-05 early finding (queued — subagents are not
# addressable mid-flight; send via send_message on completion, BEFORE review)

APPROVED — proceed exactly as proposed (ImageRuleAnalyzer takes over the alt
check under AuditCategory.Images, same key `accessibility.image-alt`, same
semantics; production AccessibilityAnalyzer disabled via
RuleOverrides["accessibility.image-alt"] = new(Enabled: false)).

Verified against the code:
- AccessibilityAnalyzer.cs:86 gates ImageAltRule on enabledKeys;
  AccessibilityAnalyzerTests already exercises the exact override shape.
- Wiring dependency recorded in STEP-4-WIRING-PLAN.md §4: coordinator sets
  the override at the production DI construction (and checks the
  SinglePageTester path for a second construction).

Clarifications:
1. `accessibility.image-alt` is NOT in the locked FindingRuleKeys catalog —
   it lives in AccessibilityRuleKeys (producer-local, outside Contracts).
   Contracts are re-locked: keep emitting the exact string via
   AccessibilityRuleKeys.ImageAlt (or an equal constant in your own file);
   do NOT add it to FindingRuleKeys, do NOT rename it — the harness verdict
   t_missing_alt matches that exact key. The "replace builder-local keys"
   rule applies to keys that exist in FindingRuleKeys; stability wins here.
   Note it in the handoff.
2. Preserve the full exemption semantics (decorative role/aria-hidden/
   empty-alt/figcaption, input[type=image], area) and the Error severity +
   locator/remediation evidence shape the accessibility tests assert — the
   Images-category emission must be evidence-compatible.
3. Any out-of-ownership test asserting the alt finding's category goes in
   the handoff, not into edits (none found in coordinator grep).