# Phase 8 - statistics summary

Data: `results/results.csv` (tag phase7-results), 15,360 rows, 32 controllers x 4 maps x 4 noise levels x 30 seeds. Bootstrap seed 20270101, 5000 resamples.
Unit of analysis for chatbot / OS-vs-FB comparisons: the ATTEMPT (5 per chatbot, 15 per condition). Trial-level Wilson intervals treat trials as independent and are therefore too narrow for the LLM groups; the cluster-bootstrap columns resample controllers instead. The two no_compile files (ChatGPT 1 OS, ChatGPT 3 OS) count as success 0 in every rate.

## A1. Success rate (Wilson 95% CI, trial level; cluster-bootstrap CI over controllers for LLM groups)

| group | overall | M1_Open | M2_Cluttered | M3_Corridor | M4_Trap |
|---|---|---|---|---|---|
| Rule | 75.0 [70.9, 78.7] | 100.0 [96.9, 100.0] | 100.0 [96.9, 100.0] | 100.0 [96.9, 100.0] | 0.0 [0.0, 3.1] |
| Bug2 | 16.2 [13.2, 19.8] | 0.0 [0.0, 3.1] | 0.0 [0.0, 3.1] | 0.8 [0.1, 4.6] | 64.2 [55.3, 72.2] |
| ChatGPT OS | 10.6 [9.5, 11.9] | 22.5 [19.3, 26.0] | 0.0 [0.0, 0.6] | 20.0 [17.0, 23.4] | 0.0 [0.0, 0.6] |
| ChatGPT FB | 5.3 [4.5, 6.3] | 21.3 [18.2, 24.8] | 0.0 [0.0, 0.6] | 0.0 [0.0, 0.6] | 0.0 [0.0, 0.6] |
| Gemini OS | 30.6 [28.8, 32.5] | 72.0 [68.3, 75.4] | 4.7 [3.2, 6.7] | 28.3 [24.9, 32.1] | 17.5 [14.7, 20.7] |
| Gemini FB | 21.2 [19.6, 22.9] | 45.2 [41.2, 49.2] | 7.5 [5.7, 9.9] | 32.0 [28.4, 35.8] | 0.2 [0.0, 0.9] |
| Claude OS | 76.6 [74.9, 78.3] | 96.0 [94.1, 97.3] | 87.8 [85.0, 90.2] | 59.8 [55.9, 63.7] | 62.8 [58.9, 66.6] |
| Claude FB | 75.3 [73.5, 77.0] | 100.0 [99.4, 100.0] | 80.3 [77.0, 83.3] | 60.0 [56.0, 63.8] | 60.8 [56.9, 64.7] |
| LLM OS | 39.3 [38.2, 40.4] | 63.5 [61.2, 65.7] | 30.8 [28.7, 33.0] | 36.1 [33.9, 38.3] | 26.8 [24.8, 28.9] |
| LLM FB | 33.9 [32.9, 35.0] | 55.5 [53.2, 57.8] | 29.3 [27.2, 31.4] | 30.7 [28.6, 32.8] | 20.3 [18.5, 22.3] |

Cluster-bootstrap 95% CI (overall, resampling controllers):

| group | rate_pct | cluster_boot_lo_pct | cluster_boot_hi_pct |
|---|---|---|---|
| ChatGPT OS | 10.6 | 0.0 | 30.6 |
| ChatGPT FB | 5.3 | 1.1 | 9.9 |
| Gemini OS | 30.6 | 17.5 | 48.1 |
| Gemini FB | 21.2 | 6.3 | 35.8 |
| Claude OS | 76.6 | 68.2 | 88.0 |
| Claude FB | 75.3 | 65.2 | 88.0 |
| LLM OS | 39.3 | 23.4 | 55.6 |
| LLM FB | 33.9 | 18.0 | 51.0 |

## A2. Outcome breakdown (%)

| group | trials | success_pct | collision_pct | timeout_pct | no_compile_pct |
|---|---|---|---|---|---|
| Rule | 480 | 75.0 | 0.0 | 25.0 | 0.0 |
| Bug2 | 480 | 16.2 | 59.2 | 24.6 | 0.0 |
| ChatGPT OS | 2400 | 10.6 | 2.9 | 46.5 | 40.0 |
| ChatGPT FB | 2400 | 5.3 | 6.6 | 88.1 | 0.0 |
| Gemini OS | 2400 | 30.6 | 0.1 | 69.2 | 0.0 |
| Gemini FB | 2400 | 21.2 | 0.2 | 78.6 | 0.0 |
| Claude OS | 2400 | 76.6 | 15.8 | 7.6 | 0.0 |
| Claude FB | 2400 | 75.3 | 14.0 | 10.7 | 0.0 |
| LLM OS | 7200 | 39.3 | 6.3 | 41.1 | 13.3 |
| LLM FB | 7200 | 33.9 | 6.9 | 59.1 | 0.0 |

## A3. Secondary metrics (successful runs only), median per family x noise

| family | noise | n_success | median_time_s | median_path_m | median_efficiency | median_min_clear_cm |
|---|---|---|---|---|---|---|
| Rule | 0.00 | 90 | 27.80 | 7.25 | 0.89 | 9.15 |
| Rule | 0.02 | 90 | 27.66 | 7.26 | 0.88 | 9.00 |
| Rule | 0.05 | 90 | 27.43 | 7.24 | 0.89 | 8.90 |
| Rule | 0.10 | 90 | 28.12 | 7.28 | 0.88 | 9.05 |
| Bug2 | 0.00 | 27 | 57.36 | 11.28 | 0.56 | 11.20 |
| Bug2 | 0.02 | 17 | 57.72 | 11.27 | 0.57 | 10.60 |
| Bug2 | 0.05 | 22 | 57.24 | 11.26 | 0.56 | 10.95 |
| Bug2 | 0.10 | 12 | 57.74 | 11.26 | 0.56 | 10.25 |
| LLM-OS | 0.00 | 709 | 37.98 | 7.10 | 0.91 | 7.50 |
| LLM-OS | 0.02 | 707 | 38.10 | 7.13 | 0.91 | 7.60 |
| LLM-OS | 0.05 | 707 | 38.60 | 7.13 | 0.91 | 7.70 |
| LLM-OS | 0.10 | 706 | 38.87 | 7.17 | 0.90 | 8.10 |
| LLM-FB | 0.00 | 619 | 37.06 | 7.18 | 0.90 | 7.70 |
| LLM-FB | 0.02 | 608 | 36.86 | 7.22 | 0.89 | 7.60 |
| LLM-FB | 0.05 | 606 | 36.97 | 7.15 | 0.90 | 7.70 |
| LLM-FB | 0.10 | 611 | 37.32 | 7.21 | 0.90 | 8.20 |

## B1. Noise effect

**LLM (28 compiled controllers, 13440 simulated rows)**, success ~ noise + map + (1|controller). Odds ratio per +0.10 noise.

- Random-intercept logistic (variational Bayes, `BinomialBayesMixedGLM`): OR = 0.978 (95% interval 0.892 - 1.072); controller-intercept SD (log-odds) = 3.67.
- Cross-check, GEE logistic clustered by controller (robust SE): OR = 0.989 (95% CI 0.878 - 1.114), p = 0.857.

**Rule**: successes per map x noise (out of 30 each):

| map | 0.0 | 0.02 | 0.05 | 0.1 |
|---|---|---|---|---|
| M1_Open | 30 | 30 | 30 | 30 |
| M2_Cluttered | 30 | 30 | 30 | 30 |
| M3_Corridor | 30 | 30 | 30 | 30 |
| M4_Trap | 0 | 0 | 0 | 0 |

- No map has any variation in outcome, so a logistic regression is not defined; success is identical at all four noise levels in every map (odds ratio per +0.10 = 1 by construction, 0 outcome changes attributable to noise).

**Bug2**: successes per map x noise (out of 30 each):

| map | 0.0 | 0.02 | 0.05 | 0.1 |
|---|---|---|---|---|
| M1_Open | 0 | 0 | 0 | 0 |
| M2_Cluttered | 0 | 0 | 0 | 0 |
| M3_Corridor | 0 | 0 | 0 | 1 |
| M4_Trap | 27 | 17 | 22 | 11 |

- Logistic regression on the maps with outcome variation (M3_Corridor, M4_Trap; the other maps are all-0 or all-1 and are dropped): OR per +0.10 noise = 0.182 (95% CI 0.065 - 0.507), p = 0.0011.

- LLM successful runs (n=5273), linear mixed model time_s ~ noise + map + (1|controller): effect per +0.10 noise = +0.370 (95% CI +0.077 to +0.663), p = 0.0133.
- LLM successful runs (n=5273), linear mixed model min_clear_cm ~ noise + map + (1|controller): effect per +0.10 noise = +0.474 (95% CI +0.249 to +0.699), p = 0.0000.

- Rule successful runs (n=360), OLS time_s ~ noise + map: +0.913 per +0.10 noise (95% CI +0.198 to +1.627), p = 0.0123 (trials of one controller, HC3 SE).
- Rule successful runs (n=360), OLS min_clear_cm ~ noise + map: -0.019 per +0.10 noise (95% CI -0.444 to +0.407), p = 0.9321 (trials of one controller, HC3 SE).
- Bug2 successful runs (n=78), OLS time_s ~ noise + map: +0.186 per +0.10 noise (95% CI -0.591 to +0.963), p = 0.6387 (trials of one controller, HC3 SE).
- Bug2 successful runs (n=78), OLS min_clear_cm ~ noise + map: -0.644 per +0.10 noise (95% CI -2.373 to +1.085), p = 0.4652 (trials of one controller, HC3 SE).

## B2. OS vs FB (unit = attempt, 15 pairs)

| bot | attempt | os_rate | fb_rate | diff_fb_minus_os | os_nc | fb_nc | identical_code_copy |
|---|---|---|---|---|---|---|---|
| ChatGPT | 1 | 0.0 | 0.0 | 0.0 | 1 | 0 | False |
| ChatGPT | 2 | 50.0 | 14.2 | -35.8 | 0 | 0 | False |
| ChatGPT | 3 | 0.0 | 5.6 | 5.6 | 1 | 0 | False |
| ChatGPT | 4 | 0.0 | 0.0 | 0.0 | 0 | 0 | False |
| ChatGPT | 6 | 3.1 | 6.9 | 3.8 | 0 | 0 | False |
| Gemini | 1 | 17.9 | 49.4 | 31.5 | 0 | 0 | False |
| Gemini | 2 | 33.3 | 25.4 | -7.9 | 0 | 0 | False |
| Gemini | 3 | 64.4 | 25.4 | -39.0 | 0 | 0 | False |
| Gemini | 4 | 14.2 | 0.2 | -14.0 | 0 | 0 | False |
| Gemini | 5 | 23.3 | 5.6 | -17.7 | 0 | 0 | False |
| Claude | 1 | 68.5 | 68.5 | 0.0 | 0 | 0 | True |
| Claude | 2 | 75.8 | 75.8 | 0.0 | 0 | 0 | True |
| Claude | 3 | 65.6 | 59.0 | -6.7 | 0 | 0 | False |
| Claude | 4 | 74.6 | 74.6 | 0.0 | 0 | 0 | True |
| Claude | 5 | 98.5 | 98.5 | 0.0 | 0 | 0 | True |

(rates in %; diff = FB - OS in percentage points; *_nc = 1 if that file did not compile; identical_code_copy = OS and FB source files have the same SHA-256 after renaming the class/constructor identifier and normalising line endings)

- Pairs with identical code (FB = OS copy, by source hash): Claude 1, Claude 2, Claude 4, Claude 5. Pairs whose code changed: 11.
- ALL 15 attempts: n=15 pairs, 6 tied (identical OS/FB outcome), mean diff FB-OS = -5.3 pp (bootstrap 95% CI -13.8 to +3.0); Wilcoxon signed-rank p = 0.1386, rank-biserial r = -0.56 (+ = FB better)
- ONLY pairs whose code actually changed (11 pairs): n=11 pairs, 2 tied (identical OS/FB outcome), mean diff FB-OS = -7.3 pp (bootstrap 95% CI -18.6 to +4.1); Wilcoxon signed-rank p = 0.1641, rank-biserial r = -0.56 (+ = FB better)
- ChatGPT: n=5 pairs, 2 tied (identical OS/FB outcome), mean diff FB-OS = -5.3 pp (bootstrap 95% CI -20.8 to +3.8); Wilcoxon signed-rank p = 1.0000, rank-biserial r = +0.00 (+ = FB better) [descriptive, n=5]
- Gemini: n=5 pairs, 0 tied (identical OS/FB outcome), mean diff FB-OS = -9.4 pp (bootstrap 95% CI -27.8 to +12.5); Wilcoxon signed-rank p = 0.4375, rank-biserial r = -0.47 (+ = FB better) [descriptive, n=5]
- Claude: n=5 pairs, 4 tied (identical OS/FB outcome), mean diff FB-OS = -1.3 pp (bootstrap 95% CI -4.0 to +0.0); Wilcoxon signed-rank p = 1.0000, rank-biserial r = -1.00 (+ = FB better) [descriptive, n=5]
- Sensitivity: only pairs where both files compile (13 pairs): n=13 pairs, 5 tied (identical OS/FB outcome), mean diff FB-OS = -6.6 pp (bootstrap 95% CI -16.4 to +2.6); Wilcoxon signed-rank p = 0.1484, rank-biserial r = -0.61 (+ = FB better)
- Note: 4 of 15 pairs are identical copies (zero difference by construction, tied in the all-15 test). Among the 11 changed pairs, 2 have exactly equal success rates by chance.
- Trial-level McNemar (secondary; 7200 matched trials, trials are not independent): OS-only successes = 734, FB-only successes = 349, exact p = 4.104e-32. Odds ratio (FB/OS discordant) = 0.48.

## B3. Chatbots (per-attempt OS success rates; n = 5 per group is SMALL, treat as descriptive/exploratory)

- Kruskal-Wallis on 3 x 5 per-attempt OS success rates: H = 10.90, p = 0.0043, epsilon-squared = 0.74. Group medians (%): ChatGPT 0.0, Gemini 23.3, Claude 74.6.
- ChatGPT vs Gemini: Mann-Whitney exact p = 0.0952, Holm-adjusted p = 0.0952, Cliff's delta = -0.68
- ChatGPT vs Claude: Mann-Whitney exact p = 0.0079, Holm-adjusted p = 0.0238, Cliff's delta = -1.00
- Gemini vs Claude: Mann-Whitney exact p = 0.0079, Holm-adjusted p = 0.0238, Cliff's delta = -1.00
- Same on FB rates (secondary): H = 10.41, p = 0.0055.

## B4. LLM controllers vs Rule, per map (noise pooled; each LLM controller's success rate on the map vs Rule's)

| map | rule_pct | llm_match_or_beat | llm_strictly_beat | llm_below | n_llm | llm_median_pct | llm_best_pct |
|---|---|---|---|---|---|---|---|
| M1_Open | 100.0 | 13 | 0 | 17 | 30 | 80.8 | 100.0 |
| M2_Cluttered | 100.0 | 2 | 0 | 28 | 30 | 1.2 | 100.0 |
| M3_Corridor | 100.0 | 5 | 0 | 25 | 30 | 2.5 | 100.0 |
| M4_Trap | 0.0 | 30 | 10 | 0 | 30 | 0.0 | 100.0 |

(M4: Rule scores 0%, so every controller trivially matches it; 'strictly beat' is the meaningful column there.)

## Key numbers

- Overall success: Rule 75.0%, Bug2 16.2%, LLM-OS 39.3%, LLM-FB 33.9%.
- Best / worst controller per chatbot (overall success, compiled files, ties broken by name): ChatGPT best LLM_ChatGPT_2_OS (50.0%), worst LLM_ChatGPT_4_OS (0.0%); Gemini best LLM_Gemini_3_OS (64.4%), worst LLM_Gemini_4_FB (0.2%); Claude best LLM_Claude_5_FB (98.5%), worst LLM_Claude_3_FB (59.0%)
- Figure 1 caption note: Rule and Bug2 bands = trial-level Wilson 95% intervals; LLM bands = cluster bootstrap over controllers (95%).
- Figure 3 note: 4 Claude FB = OS (identical code); lines are jittered slightly in x so overlapping lines stay visible.
- Figure 4 panels are drawn from results/paths (saved for M4, noise 0.05, seeds 1-3 only).

Outputs: tables A1-A3, B2, B4 (CSV + LaTeX), figures fig1-fig5 (PDF + PNG 300 dpi) and data files in `analysis/out/`.
