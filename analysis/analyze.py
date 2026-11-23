"""analyze.py - Phase 8: statistics and figures for the RoverNavStudy paper.

Reads results/results.csv (frozen at tag phase7-results; NEVER modified here) and results/paths/*.csv, and writes everything to
analysis/out/ (tables as CSV + LaTeX, figures as PDF + PNG 300 dpi, summary.md). Run:  python analysis/analyze.py
Reproducible: fixed bootstrap seed (BOOT_SEED), no other randomness.

Unit of analysis: trials are nested in controllers. Comparisons between chatbots or OS vs FB use the ATTEMPT (5 per chatbot) as the
unit. Trials are used only for within-controller effects (noise, map) and, with a controller random effect, for the noise models.
Missing compile = failure: the two no_compile files count as success 0 in every success rate (intention-to-treat), but they are
excluded from models/metrics that need a simulated run (noise models, secondary metrics).
"""
import math, os, re, sys, warnings
import numpy as np
import pandas as pd
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib.patches import Polygon, Rectangle
from scipy import stats
import statsmodels.api as sm
import statsmodels.formula.api as smf

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "analysis", "out")
BOOT_SEED, N_BOOT = 20270101, 5000
MAPS = ["M1_Open", "M2_Cluttered", "M3_Corridor", "M4_Trap"]
NOISES = [0.00, 0.02, 0.05, 0.10]
BOTS = ["ChatGPT", "Gemini", "Claude"]
warnings.filterwarnings("ignore")
os.makedirs(OUT, exist_ok=True)
SUMMARY = []           # lines of summary.md


def say(s=""):
    SUMMARY.append(s)


# ------------------------------------------------------------------ helpers
def wilson(k, n, z=1.959964):
    if n == 0:
        return (np.nan, np.nan)
    p = k / n
    d = 1 + z * z / n
    c = (p + z * z / (2 * n)) / d
    h = z * math.sqrt(p * (1 - p) / n + z * z / (4 * n * n)) / d
    return (c - h, c + h)


def holm(ps):
    order = np.argsort(ps)
    m = len(ps)
    adj = np.empty(m)
    run = 0.0
    for rank, i in enumerate(order):
        run = max(run, (m - rank) * ps[i])
        adj[i] = min(1.0, run)
    return adj


def latex_table(df, path, caption="", fmt=None):
    cols = list(df.columns)
    esc = lambda s: str(s).replace("_", r"\_").replace("%", r"\%").replace("&", r"\&")
    lines = [r"\begin{tabular}{" + "l" * len(cols) + "}", r"\hline", " & ".join(esc(c) for c in cols) + r" \\", r"\hline"]
    for _, r in df.iterrows():
        cells = []
        for c in cols:
            v = r[c]
            if isinstance(v, float):
                cells.append("--" if np.isnan(v) else (fmt.get(c, "%.3f") % v if fmt else "%.3f" % v))
            else:
                cells.append(esc(v))
        lines.append(" & ".join(cells) + r" \\")
    lines += [r"\hline", r"\end{tabular}"]
    open(path, "w", encoding="utf-8").write("\n".join(lines) + "\n")


def save_table(df, name, caption=""):
    df.to_csv(os.path.join(OUT, name + ".csv"), index=False)
    latex_table(df, os.path.join(OUT, name + ".tex"), caption)


def md_table(df, floatfmt="%.3f"):
    cols = list(df.columns)
    out = ["| " + " | ".join(str(c) for c in cols) + " |", "|" + "---|" * len(cols)]
    for _, r in df.iterrows():
        out.append("| " + " | ".join((floatfmt % v) if isinstance(v, float) and not np.isnan(v) else ("" if isinstance(v, float) else str(v)) for v in r) + " |")
    return "\n".join(out)


def cluster_boot_rate(sub, rng, n_boot=N_BOOT):
    """percentile CI of a pooled success rate, resampling CONTROLLERS (not trials) with replacement"""
    g = sub.groupby("controller")["success"].agg(["sum", "count"])
    if len(g) < 2:
        return (np.nan, np.nan)
    k, n = g["sum"].values, g["count"].values
    idx = rng.integers(0, len(g), size=(n_boot, len(g)))
    r = k[idx].sum(1) / n[idx].sum(1)
    return (np.percentile(r, 2.5), np.percentile(r, 97.5))


def boot_mean_ci(x, rng, n_boot=N_BOOT):
    x = np.asarray(x, float)
    m = x[rng.integers(0, len(x), size=(n_boot, len(x)))].mean(1)
    return (np.percentile(m, 2.5), np.percentile(m, 97.5))


# ------------------------------------------------------------------ data
df = pd.read_csv(os.path.join(ROOT, "results", "results.csv"), na_values=["NA"], keep_default_na=False)
for c in ["success", "collision", "timeout", "seed", "noise"]:
    df[c] = pd.to_numeric(df[c])
df["bot"] = df["model_label"].map(lambda s: s.split()[0] if s.startswith(("ChatGPT", "Gemini", "Claude")) else s)
df["cond"] = df["condition"].where(df["family"] == "LLM", "")
df["grp"] = np.where(df["family"] == "LLM", df["bot"] + " " + df["cond"], df["family"])
df["noise"] = df["noise"].round(2)
df["nc"] = (df["outcome"] == "no_compile").astype(int)
assert len(df) == 15360 and (df["outcome"] != "hang").all()
sim = df[df["nc"] == 0]
rng = np.random.default_rng(BOOT_SEED)
LLM = df[df["family"] == "LLM"]
GROUPS = ["Rule", "Bug2"] + [b + " " + c for b in BOTS for c in ("OS", "FB")]

say("# Phase 8 - statistics summary")
say("")
say("Data: `results/results.csv` (tag phase7-results), 15,360 rows, 32 controllers x 4 maps x 4 noise levels x 30 seeds. Bootstrap seed %d, %d resamples." % (BOOT_SEED, N_BOOT))
say("Unit of analysis for chatbot / OS-vs-FB comparisons: the ATTEMPT (5 per chatbot, 15 per condition). Trial-level Wilson intervals treat trials as independent and are therefore too narrow for the LLM groups; the cluster-bootstrap columns resample controllers instead. The two no_compile files (ChatGPT 1 OS, ChatGPT 3 OS) count as success 0 in every rate.")
say("")

# ================================================================== A. TABLES
# A1 success rate with Wilson CI
rows = []
scopes = [("overall", df)] + [(m, df[df["map"] == m]) for m in MAPS]
sets = [(g, df[df["grp"] == g]) for g in GROUPS] + [("LLM OS", LLM[LLM["cond"] == "OS"]), ("LLM FB", LLM[LLM["cond"] == "FB"])]
for g, sg in sets:
    for scope, _ in scopes:
        s = sg if scope == "overall" else sg[sg["map"] == scope]
        k, n = int(s["success"].sum()), len(s)
        lo, hi = wilson(k, n)
        blo, bhi = cluster_boot_rate(s, rng) if s["controller"].nunique() > 1 else (np.nan, np.nan)
        rows.append(dict(group=g, scope=scope, controllers=s["controller"].nunique(), successes=k, trials=n, rate_pct=100 * k / n,
                         wilson_lo_pct=100 * lo, wilson_hi_pct=100 * hi, cluster_boot_lo_pct=100 * blo, cluster_boot_hi_pct=100 * bhi))
A1 = pd.DataFrame(rows)
save_table(A1, "A1_success_rate_wilson")
say("## A1. Success rate (Wilson 95% CI, trial level; cluster-bootstrap CI over controllers for LLM groups)")
say("")
piv = A1.copy()
piv["cell"] = piv.apply(lambda r: "%.1f [%.1f, %.1f]" % (r.rate_pct, r.wilson_lo_pct, r.wilson_hi_pct), axis=1)
say(md_table(piv.pivot(index="group", columns="scope", values="cell").loc[[g for g, _ in sets], ["overall"] + MAPS].reset_index()))
say("")
bt = A1[(A1.scope == "overall") & A1.cluster_boot_lo_pct.notna()][["group", "rate_pct", "cluster_boot_lo_pct", "cluster_boot_hi_pct"]]
say("Cluster-bootstrap 95% CI (overall, resampling controllers):")
say("")
say(md_table(bt, "%.1f"))
say("")

# A2 failure breakdown
rows = []
for g, sg in sets:
    n = len(sg)
    rows.append(dict(group=g, trials=n, success_pct=100 * sg["success"].sum() / n, collision_pct=100 * sg["collision"].sum() / n,
                     timeout_pct=100 * sg["timeout"].sum() / n, no_compile_pct=100 * sg["nc"].sum() / n))
A2 = pd.DataFrame(rows)
save_table(A2, "A2_failure_breakdown")
say("## A2. Outcome breakdown (%)")
say("")
say(md_table(A2, "%.1f"))
say("")

# A3 secondary metrics for successful runs
succ = df[df["success"] == 1].copy()
succ["fam4"] = np.where(succ["family"] == "LLM", "LLM-" + succ["cond"], succ["family"])
rows = []
for f in ["Rule", "Bug2", "LLM-OS", "LLM-FB"]:
    for nz in NOISES:
        s = succ[(succ["fam4"] == f) & (succ["noise"] == nz)]
        rows.append(dict(family=f, noise=nz, n_success=len(s), median_time_s=s["time_s"].median(), median_path_m=s["path_m"].median(),
                         median_efficiency=s["efficiency"].median(), median_min_clear_cm=s["min_clear_cm"].median()))
A3 = pd.DataFrame(rows)
save_table(A3, "A3_secondary_metrics_successful_runs")
say("## A3. Secondary metrics (successful runs only), median per family x noise")
say("")
say(md_table(A3, "%.2f"))
say("")

# ================================================================== B. TESTS
say("## B1. Noise effect")
say("")
sim = df[df["nc"] == 0].copy()
sim["noise10"] = sim["noise"] * 10          # one unit = +0.10 noise
sl = sim[sim["family"] == "LLM"].copy()
say("**LLM (28 compiled controllers, %d simulated rows)**, success ~ noise + map + (1|controller). Odds ratio per +0.10 noise." % len(sl))
say("")
res = {}
try:
    from statsmodels.genmod.bayes_mixed_glm import BinomialBayesMixedGLM
    m = BinomialBayesMixedGLM.from_formula("success ~ noise10 + C(map)", {"ctrl": "0 + C(controller)"}, sl)
    r = m.fit_vb()
    i = list(m.fep_names).index("noise10")
    mu, sd = r.fe_mean[i], r.fe_sd[i]
    res["glmm"] = (math.exp(mu), math.exp(mu - 1.96 * sd), math.exp(mu + 1.96 * sd))
    say("- Random-intercept logistic (variational Bayes, `BinomialBayesMixedGLM`): OR = %.3f (95%% interval %.3f - %.3f); controller-intercept SD (log-odds) = %.2f." % (res["glmm"] + (math.exp(r.vcp_mean[0]),)))
except Exception as e:
    say("- Mixed logistic failed: %s" % e)
try:
    g = smf.gee("success ~ noise10 + C(map)", "controller", sl, family=sm.families.Binomial(), cov_struct=sm.cov_struct.Exchangeable()).fit()
    ci = g.conf_int().loc["noise10"]
    res["gee"] = (math.exp(g.params["noise10"]), math.exp(ci[0]), math.exp(ci[1]))
    say("- Cross-check, GEE logistic clustered by controller (robust SE): OR = %.3f (95%% CI %.3f - %.3f), p = %.3f." % (res["gee"] + (g.pvalues["noise10"],)))
except Exception as e:
    say("- GEE failed: %s" % e)
# per chatbot x condition (descriptive)
say("")
for fam in ["Rule", "Bug2"]:
    s = sim[sim["family"] == fam]
    tbl = s.groupby(["map", "noise"])["success"].sum().unstack()
    keep = [m_ for m_ in MAPS if 0 < s[s["map"] == m_]["success"].sum() < len(s[s["map"] == m_])]
    s2 = s[s["map"].isin(keep)]
    say("**%s**: successes per map x noise (out of 30 each):" % fam)
    say("")
    say(md_table(tbl.reset_index(), "%.0f"))
    say("")
    if len(keep) == 0:
        say("- No map has any variation in outcome, so a logistic regression is not defined; success is identical at all four noise levels in every map (odds ratio per +0.10 = 1 by construction, 0 outcome changes attributable to noise).")
    else:
        try:
            f = smf.logit("success ~ noise10" + (" + C(map)" if len(keep) > 1 else ""), s2).fit(disp=0)
            ci = f.conf_int().loc["noise10"]
            say("- Logistic regression on the maps with outcome variation (%s; the other maps are all-0 or all-1 and are dropped): OR per +0.10 noise = %.3f (95%% CI %.3f - %.3f), p = %.4f." % (", ".join(keep), math.exp(f.params["noise10"]), math.exp(ci[0]), math.exp(ci[1]), f.pvalues["noise10"]))
        except Exception as e:
            say("- Logistic regression failed (%s)." % e)
    say("")
succ_l = sl[sl["success"] == 1]
for metric, label in [("time_s", "time_s"), ("min_clear_cm", "min_clear_cm")]:
    try:
        m = smf.mixedlm("%s ~ noise10 + C(map)" % metric, succ_l, groups=succ_l["controller"]).fit(reml=True)
        ci = m.conf_int().loc["noise10"]
        say("- LLM successful runs (n=%d), linear mixed model %s ~ noise + map + (1|controller): effect per +0.10 noise = %+.3f (95%% CI %+.3f to %+.3f), p = %.4f." % (len(succ_l), label, m.params["noise10"], ci[0], ci[1], m.pvalues["noise10"]))
    except Exception as e:
        say("- LMM %s failed: %s" % (metric, e))
say("")
for fam in ["Rule", "Bug2"]:
    s = sim[(sim["family"] == fam) & (sim["success"] == 1)]
    if len(s) > 10:
        for metric in ["time_s", "min_clear_cm"]:
            f = smf.ols("%s ~ noise10 + C(map)" % metric, s).fit(cov_type="HC3")
            ci = f.conf_int().loc["noise10"]
            say("- %s successful runs (n=%d), OLS %s ~ noise + map: %+.3f per +0.10 noise (95%% CI %+.3f to %+.3f), p = %.4f (trials of one controller, HC3 SE)." % (fam, len(s), metric, f.params["noise10"], ci[0], ci[1], f.pvalues["noise10"]))
say("")

# ---- B2: OS vs FB per attempt
say("## B2. OS vs FB (unit = attempt, 15 pairs)")
say("")
pa = []
for bot in BOTS:
    for att in sorted(LLM[LLM["bot"] == bot]["attempt"].unique()):
        a = LLM[(LLM["bot"] == bot) & (LLM["attempt"] == att)]
        os_, fb_ = a[a["cond"] == "OS"], a[a["cond"] == "FB"]
        pa.append(dict(bot=bot, attempt=int(att), os_ctrl=os_["controller"].iloc[0], fb_ctrl=fb_["controller"].iloc[0],
                       os_rate=os_["success"].mean(), fb_rate=fb_["success"].mean(), os_nc=int(os_["nc"].max()), fb_nc=int(fb_["nc"].max())))
PA = pd.DataFrame(pa)
PA["diff_fb_minus_os"] = PA["fb_rate"] - PA["os_rate"]
PA["identical_code_copy"] = (PA["diff_fb_minus_os"] == 0)
save_table(PA, "B2_per_attempt_os_vs_fb")
say(md_table(PA.assign(os_rate=100 * PA.os_rate, fb_rate=100 * PA.fb_rate, diff_fb_minus_os=100 * PA.diff_fb_minus_os)[["bot", "attempt", "os_rate", "fb_rate", "diff_fb_minus_os", "os_nc", "fb_nc"]], "%.1f"))
say("")
say("(rates in %; diff = FB - OS in percentage points; *_nc = 1 if that file did not compile)")
say("")


def wilcox_report(d, label, descriptive=False):
    d = np.asarray(d, float)
    nz = d[d != 0]
    line = "- %s: n=%d pairs, %d tied (identical OS/FB outcome), mean diff FB-OS = %+.1f pp" % (label, len(d), len(d) - len(nz), 100 * d.mean())
    if len(d) > 1:
        lo, hi = boot_mean_ci(d, rng)
        line += " (bootstrap 95%% CI %+.1f to %+.1f)" % (100 * lo, 100 * hi)
    if len(nz) >= 1:
        ranks = stats.rankdata(np.abs(nz))
        wp, wm = ranks[nz > 0].sum(), ranks[nz < 0].sum()
        rb = (wp - wm) / (wp + wm)
        try:
            p = stats.wilcoxon(d, zero_method="wilcox").pvalue
        except Exception:
            p = np.nan
        line += "; Wilcoxon signed-rank p = %.4f, rank-biserial r = %+.2f (+ = FB better)" % (p, rb)
    if descriptive:
        line += " [descriptive, n=5]"
    say(line)


wilcox_report(PA["diff_fb_minus_os"], "ALL 15 attempts")
for bot in BOTS:
    wilcox_report(PA[PA["bot"] == bot]["diff_fb_minus_os"], bot, descriptive=True)
cmp = PA[(PA.os_nc == 0) & (PA.fb_nc == 0)]
wilcox_report(cmp["diff_fb_minus_os"], "Sensitivity: only pairs where both files compile (%d pairs)" % len(cmp))
tie = int((PA["diff_fb_minus_os"] == 0).sum())
say("- Note: %d of 15 pairs have identical OS/FB success rates (FB = OS copy when the one-shot already scored 10/10, or equal by chance)." % tie)
# McNemar trial level
key = ["bot", "attempt", "map", "noise", "seed"]
o = LLM[LLM["cond"] == "OS"].set_index(key)["success"]
f = LLM[LLM["cond"] == "FB"].set_index(key)["success"]
j = pd.concat([o.rename("os"), f.rename("fb")], axis=1)
b = int(((j.os == 1) & (j.fb == 0)).sum())
c = int(((j.os == 0) & (j.fb == 1)).sum())
pm = stats.binomtest(min(b, c), b + c, 0.5).pvalue if b + c else np.nan
say("- Trial-level McNemar (secondary; %d matched trials, trials are not independent): OS-only successes = %d, FB-only successes = %d, exact p = %.4g. Odds ratio (FB/OS discordant) = %.2f." % (len(j), b, c, pm, c / b if b else np.nan))
say("")

# ---- B3: chatbots
say("## B3. Chatbots (per-attempt OS success rates; n = 5 per group is SMALL, treat as descriptive/exploratory)")
say("")
grp_os = {bot: PA[PA["bot"] == bot]["os_rate"].values for bot in BOTS}
H, p = stats.kruskal(*grp_os.values())
N = sum(len(v) for v in grp_os.values())
eps = (H - len(BOTS) + 1) / (N - len(BOTS))
say("- Kruskal-Wallis on 3 x 5 per-attempt OS success rates: H = %.2f, p = %.4f, epsilon-squared = %.2f. Group medians (%%): %s." % (H, p, eps, ", ".join("%s %.1f" % (b_, 100 * np.median(v)) for b_, v in grp_os.items())))
pairs, ps, rows = [], [], []
for i in range(3):
    for k in range(i + 1, 3):
        u = stats.mannwhitneyu(grp_os[BOTS[i]], grp_os[BOTS[k]], alternative="two-sided", method="exact")
        delta = 2 * u.statistic / (5 * 5) - 1          # Cliff's delta (first group larger = +)
        pairs.append((BOTS[i], BOTS[k]))
        ps.append(u.pvalue)
        rows.append(delta)
adj = holm(np.array(ps))
for (a, b_), pp, pa_, dl in zip(pairs, ps, adj, rows):
    say("- %s vs %s: Mann-Whitney exact p = %.4f, Holm-adjusted p = %.4f, Cliff's delta = %+.2f" % (a, b_, pp, pa_, dl))
grp_fb = {bot: PA[PA["bot"] == bot]["fb_rate"].values for bot in BOTS}
H2, p2 = stats.kruskal(*grp_fb.values())
say("- Same on FB rates (secondary): H = %.2f, p = %.4f." % (H2, p2))
say("")

# ---- B4: LLM vs Rule per map
say("## B4. LLM controllers vs Rule, per map (noise pooled; each LLM controller's success rate on the map vs Rule's)")
say("")
rule_rate = {m: df[(df["grp"] == "Rule") & (df["map"] == m)]["success"].mean() for m in MAPS}
llm_rate = LLM.groupby(["controller", "map"])["success"].mean().unstack()
rows = []
for m in MAPS:
    r = llm_rate[m]
    rows.append(dict(map=m, rule_pct=100 * rule_rate[m], llm_match_or_beat=int((r >= rule_rate[m] - 1e-12).sum()), llm_strictly_beat=int((r > rule_rate[m] + 1e-12).sum()),
                     llm_below=int((r < rule_rate[m] - 1e-12).sum()), n_llm=len(r), llm_median_pct=100 * r.median(), llm_best_pct=100 * r.max()))
B4 = pd.DataFrame(rows)
save_table(B4, "B4_llm_vs_rule_per_map")
say(md_table(B4, "%.1f"))
say("")
say("(M4: Rule scores 0%, so every controller trivially matches it; 'strictly beat' is the meaningful column there.)")
say("")

# ================================================================== C. FIGURES
plt.rcParams.update({"font.size": 8, "axes.labelsize": 8, "axes.titlesize": 8, "legend.fontsize": 8, "xtick.labelsize": 8, "ytick.labelsize": 8,
                     "font.family": "DejaVu Sans", "axes.linewidth": 0.6, "lines.linewidth": 1.2, "pdf.fonttype": 42})
COL = {"Rule": "#000000", "Bug2": "#CC79A7", "ChatGPT": "#0072B2", "Gemini": "#E69F00", "Claude": "#009E73"}   # Okabe-Ito
MARK = {"Rule": "s", "Bug2": "D", "ChatGPT": "o", "Gemini": "^", "Claude": "v"}
W = 3.5


def savefig(fig, name):
    fig.savefig(os.path.join(OUT, name + ".pdf"), bbox_inches="tight")
    fig.savefig(os.path.join(OUT, name + ".png"), dpi=300, bbox_inches="tight")
    plt.close(fig)


# C1 success vs noise
fig, ax = plt.subplots(figsize=(W, 3.0))
rows = []
for g, sg in [(g, df[df["grp"] == g]) for g in GROUPS]:
    bot = g.split()[0]
    ls = "--" if g.endswith("FB") else "-"
    ys, los, his = [], [], []
    for nz in NOISES:
        s = sg[sg["noise"] == nz]
        k, n = int(s["success"].sum()), len(s)
        if s["controller"].nunique() > 1:
            lo, hi = cluster_boot_rate(s, np.random.default_rng(BOOT_SEED))
        else:
            lo, hi = wilson(k, n)
        ys.append(100 * k / n); los.append(100 * lo); his.append(100 * hi)
        rows.append(dict(group=g, noise=nz, rate_pct=100 * k / n, ci_lo_pct=100 * lo, ci_hi_pct=100 * hi))
    ax.fill_between(NOISES, los, his, color=COL[bot], alpha=0.08, linewidth=0)
    ax.plot(NOISES, ys, ls, color=COL[bot], marker=MARK[bot], markersize=3.5, mfc=COL[bot] if ls == "-" else "white", label=g)
pd.DataFrame(rows).to_csv(os.path.join(OUT, "C1_success_vs_noise_data.csv"), index=False)
ax.set_xlabel("sensor noise (fraction of distance)")
ax.set_ylabel("success rate (%)")
ax.set_xticks(NOISES)
ax.set_ylim(-2, 102)
ax.grid(alpha=0.3, linewidth=0.4)
ax.legend(ncol=2, loc="upper center", bbox_to_anchor=(0.5, -0.22), frameon=False, handlelength=2.2, columnspacing=1.0)
savefig(fig, "fig1_success_vs_noise")

# C2 heatmap controllers x maps
order = ["RuleBasedController", "Bug2Controller"]
labels = ["Rule", "Bug2"]
for bot in BOTS:
    for cnd in ("OS", "FB"):
        for att in sorted(LLM[LLM["bot"] == bot]["attempt"].unique()):
            order.append("LLM_%s_%d_%s" % (bot, att, cnd))
            labels.append("%s %d %s" % (bot, att, cnd))
hm = df.groupby(["controller", "map"])["success"].mean().unstack().loc[order, MAPS]
ncset = set(df[df["nc"] == 1]["controller"])
fig, ax = plt.subplots(figsize=(W, 5.9))
im = ax.imshow(100 * hm.values, cmap="viridis", vmin=0, vmax=100, aspect="auto")
ax.set_xticks(range(4)); ax.set_xticklabels(["M1", "M2", "M3", "M4"])
ax.xaxis.tick_top()
ax.set_yticks(range(len(order))); ax.set_yticklabels(labels)
for i, c in enumerate(order):
    for j_, m in enumerate(MAPS):
        v = 100 * hm.iloc[i, j_]
        ax.text(j_, i, "NC" if c in ncset else "%d" % round(v), ha="center", va="center", fontsize=8, color="white" if v < 60 else "black")
for y in [1.5, 6.5, 11.5, 16.5, 21.5, 26.5]:
    ax.axhline(y, color="white", linewidth=1.6)
ax.tick_params(length=0)
cb = fig.colorbar(im, ax=ax, fraction=0.05, pad=0.03)
cb.set_label("success rate (%)")
hm.assign(controller=hm.index).to_csv(os.path.join(OUT, "C2_heatmap_data.csv"), index=False)
savefig(fig, "fig2_heatmap")

# C3 slope chart OS -> FB
fig, ax = plt.subplots(figsize=(W, 3.0))
for _, r in PA.iterrows():
    ax.plot([0, 1], [100 * r.os_rate, 100 * r.fb_rate], "-", color=COL[r.bot], marker=MARK[r.bot], markersize=3.5, alpha=0.85)
ax.set_xticks([0, 1]); ax.set_xticklabels(["OS (one-shot)", "FB (feedback)"])
ax.set_xlim(-0.25, 1.25)
ax.set_ylabel("success rate per attempt (%)")
ax.grid(alpha=0.3, linewidth=0.4)
for b_ in BOTS:
    ax.plot([], [], "-", color=COL[b_], marker=MARK[b_], markersize=3.5, label=b_)
ax.legend(frameon=False, loc="upper center", ncol=3, bbox_to_anchor=(0.5, 1.14), columnspacing=1.0, handlelength=1.8)
savefig(fig, "fig3_os_vs_fb_slope")

# C4 example paths on M4, noise 0.05, seed 1
asset = open(os.path.join(ROOT, "Assets", "Maps", "M4_Trap.asset"), encoding="utf-8").read()
obst = re.findall(r"position: \{x: ([-\d.]+), y: ([-\d.]+)\}\s+rotationDeg: ([-\d.]+)\s+size: \{x: ([-\d.]+), y: ([-\d.]+)\}", asset)
assert len(obst) > 0


def box_corners(x, z, deg, sx, sz):
    r = math.radians(deg)
    out = []
    for a, b_ in [(-1, -1), (1, -1), (1, 1), (-1, 1)]:
        px, pz = a * sx / 2, b_ * sz / 2
        out.append((x + px * math.cos(r) - pz * math.sin(r), z + px * math.sin(r) + pz * math.cos(r)))
    return out


per_ctrl = LLM.groupby("controller")["success"].mean()
compiled = [c for c in per_ctrl.index if c not in ncset]
sel = [("Rule", "RuleBasedController"), ("Bug2", "Bug2Controller")]
bestworst = {}
for bot in BOTS:
    cs = sorted([c for c in compiled if c.startswith("LLM_%s_" % bot)], key=lambda c: (-per_ctrl[c], c))
    bestworst[bot] = (cs[0], cs[-1])
    sel.append((bot + " best", cs[0]))
    sel.append((bot + " worst", cs[-1]))
m4 = df[(df["map"] == "M4_Trap") & (df["noise"] == 0.05) & (df["seed"] == 1)].set_index("controller")
fig, axes = plt.subplots(4, 2, figsize=(W, 6.9))
for ax, (title, c) in zip(axes.ravel(), sel):
    for x, z, deg, sx, sz in obst:
        ax.add_patch(Polygon(box_corners(float(x), float(z), float(deg), float(sx), float(sz)), closed=True, fc="#999999", ec="#555555", lw=0.4))
    ax.add_patch(Rectangle((-3, -3), 6, 6, fill=False, ec="black", lw=0.8))
    ax.add_patch(Rectangle((-3, -3), 1, 1, fc="#56B4E9", alpha=0.35, ec="none"))
    ax.add_patch(Rectangle((2, 2), 1, 1, fc="#009E73", alpha=0.35, ec="none"))
    p = pd.read_csv(os.path.join(ROOT, "results", "paths", "%s_M4_Trap_n0.05_s1.csv" % c))
    ax.plot(p["x_m"], p["z_m"], "-", color="#D55E00", lw=0.9)
    ax.plot(p["x_m"].iloc[0], p["z_m"].iloc[0], "o", color="black", ms=2.5)
    ax.plot(p["x_m"].iloc[-1], p["z_m"].iloc[-1], "x", color="black", ms=4, mew=1.0)
    ax.set_xlim(-3.1, 3.1); ax.set_ylim(-3.1, 3.1); ax.set_aspect("equal"); ax.set_xticks([]); ax.set_yticks([])
    short = c.replace("LLM_", "").replace("Controller", "").replace("BasedController", "")
    ax.set_title("%s\n%s: %s" % (title, short, m4.loc[c, "outcome"]), fontsize=8)
fig.subplots_adjust(hspace=0.55, wspace=0.15)
savefig(fig, "fig4_example_paths_M4")
pd.DataFrame([dict(panel=t, controller=c, outcome=m4.loc[c, "outcome"], overall_success_pct=100 * per_ctrl[c] if c in per_ctrl.index else np.nan) for t, c in sel]).to_csv(os.path.join(OUT, "C4_path_panels.csv"), index=False)

# C5 failure-type stacked bars
fig, ax = plt.subplots(figsize=(W, 2.9))
bars = [("Rule", "Rule"), ("Bug2", "Bug2")] + [("%s %s" % (b_, c_), "%s %s" % (b_, c_)) for b_ in BOTS for c_ in ("OS", "FB")]
A2i = A2.set_index("group")
comp = [("success_pct", "success", "#009E73", ""), ("collision_pct", "collision", "#D55E00", "//"), ("timeout_pct", "timeout", "#E69F00", ".."), ("no_compile_pct", "no compile", "#555555", "xx")]
x = np.arange(len(bars))
bottom = np.zeros(len(bars))
for col, lab, colr, hat in comp:
    v = np.array([A2i.loc[g, col] for g, _ in bars])
    ax.bar(x, v, bottom=bottom, color=colr, hatch=hat, edgecolor="white", linewidth=0.4, label=lab, width=0.75)
    bottom += v
ax.set_xticks(x); ax.set_xticklabels([b_[1] for b_ in bars], rotation=45, ha="right")
ax.set_ylabel("share of trials (%)"); ax.set_ylim(0, 100)
ax.legend(ncol=4, loc="upper center", bbox_to_anchor=(0.5, 1.2), frameon=False, columnspacing=0.8, handlelength=1.3)
savefig(fig, "fig5_failure_types")

# ================================================================== summary extras
say("## Key numbers")
say("")
say("- Overall success: Rule %.1f%%, Bug2 %.1f%%, LLM-OS %.1f%%, LLM-FB %.1f%%." % tuple(A1[(A1.scope == "overall") & (A1.group == g)]["rate_pct"].iloc[0] for g in ["Rule", "Bug2", "LLM OS", "LLM FB"]))
say("- Best / worst controller per chatbot (overall success, compiled files, ties broken by name): " + "; ".join("%s best %s (%.1f%%), worst %s (%.1f%%)" % (b_, bw[0], 100 * per_ctrl[bw[0]], bw[1], 100 * per_ctrl[bw[1]]) for b_, bw in bestworst.items()))
say("- Figure 4 panels are drawn from results/paths (saved for M4, noise 0.05, seeds 1-3 only).")
say("")
say("Outputs: tables A1-A3, B2, B4 (CSV + LaTeX), figures fig1-fig5 (PDF + PNG 300 dpi) and data files in `analysis/out/`.")
open(os.path.join(OUT, "summary.md"), "w", encoding="utf-8").write("\n".join(SUMMARY) + "\n")
print("done; wrote", len(os.listdir(OUT)), "files to", OUT)
