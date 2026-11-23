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

## S. Stress test (EXPLORATORY, Phase 8b; not part of the pre-planned analysis)

Data: `results/results_stress.csv` (11,520 rows = 32 controllers x 4 maps x 30 seeds x 3 conditions: noise 0.20 / dropout 2 %, noise 0.30 / dropout 2 %, noise 0.05 / dropout 10 %). Noise 0, 0.05 and 0.10 come from the main data, so the main run is unchanged. Noise levels above 0.10 lie outside the pre-registered design range. Bootstrap seed 20270109, 5000 resamples (own generator).

### S1. Success rate (%) by noise level, dropout 2 % (Wilson 95% CI for Rule / Bug2, trial level; cluster-bootstrap 95% CI over controllers for the LLM groups; all maps pooled, no_compile counted as 0)

| group | noise 0.00 | noise 0.05 | noise 0.10 | noise 0.20 | noise 0.30 |
|---|---|---|---|---|---|
| Rule | 75.0 [66.6, 81.9] | 75.0 [66.6, 81.9] | 75.0 [66.6, 81.9] | 75.0 [66.6, 81.9] | 75.0 [66.6, 81.9] |
| Bug2 | 22.5 [15.9, 30.8] | 18.3 [12.4, 26.2] | 10.0 [5.8, 16.7] | 5.8 [2.9, 11.6] | 6.7 [3.4, 12.6] |
| ChatGPT OS | 10.0 [0.0, 30.0] | 10.3 [0.0, 30.3] | 11.7 [0.0, 31.7] | 12.0 [0.0, 32.7] | 7.2 [0.0, 20.8] |
| ChatGPT FB | 5.7 [1.5, 9.8] | 5.5 [1.3, 9.8] | 4.8 [0.3, 11.5] | 1.7 [0.0, 4.0] | 0.2 [0.0, 0.5] |
| Gemini OS | 31.5 [20.0, 48.8] | 30.5 [16.5, 48.5] | 29.2 [14.0, 46.7] | 23.5 [10.3, 37.0] | 20.8 [11.0, 29.3] |
| Gemini FB | 22.2 [7.0, 37.3] | 21.0 [6.5, 35.8] | 19.2 [6.0, 34.8] | 12.7 [3.2, 22.2] | 12.8 [3.2, 23.3] |
| Claude OS | 76.7 [67.7, 88.5] | 77.0 [69.0, 88.5] | 76.8 [65.2, 89.3] | 76.0 [60.5, 91.5] | 57.0 [37.3, 77.2] |
| Claude FB | 75.3 [65.8, 87.3] | 74.5 [62.0, 88.5] | 77.8 [68.2, 89.2] | 73.0 [56.1, 91.5] | 50.5 [26.8, 74.2] |
| LLM OS | 39.4 [23.4, 55.6] | 39.3 [22.9, 55.9] | 39.2 [23.0, 56.3] | 37.2 [21.3, 54.5] | 28.3 [15.6, 42.4] |
| LLM FB | 34.4 [18.2, 50.8] | 33.7 [17.7, 50.4] | 33.9 [17.7, 51.8] | 29.1 [12.8, 47.6] | 21.2 [8.6, 36.4] |

(noise 0.20 and 0.30 are the exploratory stress levels; noise 0.02 is in `S1_stress_success_by_noise.csv` and fig6.)

Success rate (%) per map (M1 open, M2 cluttered, M3 corridor, M4 trap) at noise 0.05 / 0.10 / 0.20 / 0.30:

| family | M1 n0.05 | M2 n0.05 | M3 n0.05 | M4 n0.05 | M1 n0.10 | M2 n0.10 | M3 n0.10 | M4 n0.10 | M1 n0.20 | M2 n0.20 | M3 n0.20 | M4 n0.20 | M1 n0.30 | M2 n0.30 | M3 n0.30 | M4 n0.30 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| Rule | 100.0 | 100.0 | 100.0 | 0.0 | 100.0 | 100.0 | 100.0 | 0.0 | 100.0 | 100.0 | 100.0 | 0.0 | 100.0 | 100.0 | 100.0 | 0.0 |
| Bug2 | 0.0 | 0.0 | 0.0 | 73.3 | 0.0 | 0.0 | 3.3 | 36.7 | 0.0 | 0.0 | 6.7 | 16.7 | 0.0 | 0.0 | 3.3 | 23.3 |
| LLM OS | 62.7 | 31.1 | 36.4 | 26.9 | 65.1 | 28.9 | 36.0 | 26.9 | 63.8 | 24.2 | 33.3 | 27.3 | 60.2 | 19.1 | 7.8 | 26.2 |
| LLM FB | 55.8 | 28.0 | 30.9 | 20.0 | 54.9 | 29.1 | 30.2 | 21.6 | 49.6 | 21.8 | 26.4 | 18.7 | 45.3 | 11.1 | 8.4 | 19.8 |

### S2. Dropout 10 % vs dropout 2 % at noise 0.05 (paired: same controller, map, start pose and sensor noise stream)

| group | trials | success_2pct | success_10pct | diff_pp | only_at_2pct | only_at_10pct | mcnemar_exact_p |
|---|---|---|---|---|---|---|---|
| Rule | 120 | 75.000 | 75.000 | 0.000 | 0 | 0 |  |
| Bug2 | 120 | 18.333 | 20.833 | 2.500 | 4 | 7 | 0.549 |
| LLM OS | 1800 | 39.278 | 38.611 | -0.667 | 56 | 44 | 0.271 |
| LLM FB | 1800 | 33.667 | 33.389 | -0.278 | 37 | 32 | 0.630 |
| LLM all | 3600 | 36.472 | 36.000 | -0.472 | 93 | 76 | 0.218 |
| ChatGPT (OS+FB) | 1200 | 7.917 | 8.083 | 0.167 | 11 | 13 | 0.839 |
| Gemini (OS+FB) | 1200 | 25.750 | 25.000 | -0.750 | 47 | 38 | 0.386 |
| Claude (OS+FB) | 1200 | 75.750 | 74.917 | -0.833 | 35 | 25 | 0.245 |

(McNemar columns: runs that succeeded only with 2 % dropout / only with 10 % dropout; trial level, trials of one controller are not independent, so the p value is descriptive.)

Controller level (unit = controller, success rate over its 120 runs; the 2 no_compile files are excluded as they are 0 in both conditions):

- LLM, compiled controllers: n=28 controllers, 15 tied, mean diff (10% - 2% dropout) = -0.5 pp (bootstrap 95% CI -1.7 to +0.5); Wilcoxon signed-rank p = 0.9164, rank-biserial r = -0.03 (+ = more success with 10% dropout)
- ChatGPT (descriptive): n=8 controllers, 5 tied, mean diff (10% - 2% dropout) = +0.2 pp (bootstrap 95% CI -0.2 to +0.7); Wilcoxon signed-rank p = 0.7500, rank-biserial r = +0.50 (+ = more success with 10% dropout)
- Gemini (descriptive): n=10 controllers, 3 tied, mean diff (10% - 2% dropout) = -0.8 pp (bootstrap 95% CI -3.3 to +1.6); Wilcoxon signed-rank p = 0.7344, rank-biserial r = -0.18 (+ = more success with 10% dropout)
- Claude (descriptive): n=10 controllers, 7 tied, mean diff (10% - 2% dropout) = -0.8 pp (bootstrap 95% CI -2.9 to +0.3); Wilcoxon signed-rank p = 1.0000, rank-biserial r = +0.00 (+ = more success with 10% dropout)

### S3. Noise effect over the extended range 0 - 0.30 (dropout 2 %)

**LLM (28 compiled controllers, 20160 simulated rows)**, success ~ noise + map + (1|controller); linear in noise on the log-odds scale. Odds ratio per +0.10 noise.

- Random-intercept logistic (variational Bayes): OR = 0.666 (95% interval 0.647 - 0.686); controller-intercept SD (log-odds) = 3.74. (Main range 0 - 0.10 only, section B1: OR = 0.978.)
- GEE logistic clustered by controller, 0 - 0.30 (n=20160): OR = 0.840 (95% CI 0.765 - 0.923), p = 0.0003.
- GEE logistic clustered by controller, main range 0 - 0.10 (n=13440): OR = 0.989 (95% CI 0.878 - 1.114), p = 0.8569.
- GEE logistic clustered by controller, exploratory part 0.10 - 0.30 (levels 0.10, 0.20, 0.30) (n=10080): OR = 0.759 (95% CI 0.659 - 0.873), p = 0.0001.

**Rule**: successes per map x noise (out of 30 each):

| map | 0.0 | 0.02 | 0.05 | 0.1 | 0.2 | 0.3 |
|---|---|---|---|---|---|---|
| M1_Open | 30 | 30 | 30 | 30 | 30 | 30 |
| M2_Cluttered | 30 | 30 | 30 | 30 | 30 | 30 |
| M3_Corridor | 30 | 30 | 30 | 30 | 30 | 30 |
| M4_Trap | 0 | 0 | 0 | 0 | 0 | 0 |

- No map has any variation in outcome: success is identical at every noise level up to 0.30.

**Bug2**: successes per map x noise (out of 30 each):

| map | 0.0 | 0.02 | 0.05 | 0.1 | 0.2 | 0.3 |
|---|---|---|---|---|---|---|
| M1_Open | 0 | 0 | 0 | 0 | 0 | 0 |
| M2_Cluttered | 0 | 0 | 0 | 0 | 0 | 0 |
| M3_Corridor | 0 | 0 | 0 | 1 | 2 | 1 |
| M4_Trap | 27 | 17 | 22 | 11 | 5 | 7 |

- Logistic regression over 0 - 0.30 on the maps with outcome variation (M3_Corridor, M4_Trap): OR per +0.10 noise = 0.442 (95% CI 0.321 - 0.609), p = 0.0000 (trials of one controller treated as independent).

Figure 6 note: success vs noise 0 - 0.30 (dropout 2 %); the shaded region above 0.10 is exploratory (outside the design range). Rule / Bug2 bands = trial-level Wilson; LLM bands = cluster bootstrap over controllers. The 10 % dropout condition is not plotted (see S2).

## Key numbers

- Overall success: Rule 75.0%, Bug2 16.2%, LLM-OS 39.3%, LLM-FB 33.9%.
- Best / worst controller per chatbot (overall success, compiled files, ties broken by name): ChatGPT best LLM_ChatGPT_2_OS (50.0%), worst LLM_ChatGPT_4_OS (0.0%); Gemini best LLM_Gemini_3_OS (64.4%), worst LLM_Gemini_4_FB (0.2%); Claude best LLM_Claude_5_FB (98.5%), worst LLM_Claude_3_FB (59.0%)
- Figure 1 caption note: Rule and Bug2 bands = trial-level Wilson 95% intervals; LLM bands = cluster bootstrap over controllers (95%).
- Figure 3 note: 4 Claude FB = OS (identical code); lines are jittered slightly in x so overlapping lines stay visible.
- Figure 4 panels are drawn from results/paths (saved for M4, noise 0.05, seeds 1-3 only).

Outputs: tables A1-A3, B2, B4 (CSV + LaTeX), figures fig1-fig5 (PDF + PNG 300 dpi) and data files in `analysis/out/`.
