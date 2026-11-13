"""Publish allowlisted validation documents after independently accepted R4 results.
Does not change a frozen binary/archive, queue tests, install, or publish a release.
"""
import datetime, hashlib, json, pathlib, re

BASE = pathlib.Path(__file__).resolve().parent.parent
REPO = BASE.parent / '20261026-file-previews-source'
OUT = BASE / 'r4-healthy-upgrade/results-v2'
DEST = REPO / 'docs/validation/0.1.45'

def read(p): return json.loads(p.read_text(encoding='utf-8-sig'))
def sha(p): return hashlib.sha256(p.read_bytes()).hexdigest().upper()
def clean(value):
    text = json.dumps(value, ensure_ascii=False, indent=2) + '\n'
    assert not re.search(r'[A-Za-z]:[\\/]|NookTester|Nook-DevTestVM|ADMIN|password|passwd|token', text, re.I)
    return text

final = read(OUT / 'final-journey-independent-review.json')
baseline = read(OUT / 'baseline-independent-review.json')
native = read(BASE / 'r4-native-regressions/results/independent-review.json')
assert final['actualUpgradeScopePassed'] and baseline['healthyBaselineScopeAccepted']
assert native['passed'] and native['checks'] == 522
assert final['candidateAppSha256'] == 'A26E4E47DF3474355622B4E5C0C31F823D3F57F1A7D8DF6BFA4221765D66FB22'
timing_path = BASE / 'r3-after1304-readonly-timing/results/independent-review.json'
timing = read(timing_path)
assert timing['diagnosticScopePassed'] and timing['original1304RetainedFailed']
prior_path = DEST / 'evidence-summary.json'
prior = read(prior_path)
assert prior['schemaVersion'] == 1
assert prior['candidateAppSha256'] == '97B122310B1033BE7EBDA63730F929C256D8D0972CBC3497C7A7D6539CCA50D6'
assert prior['candidateStatus'] == 'installation-not-qualified'
prior_sha = sha(prior_path)
private_snapshot = BASE / 'r4/evidence-summary-before-r4.json'
assert not private_snapshot.exists()
private_snapshot.write_bytes(prior_path.read_bytes())

public = dict(schemaVersion=2, generatedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(),
    candidate='R4', candidateAppSha256=final['candidateAppSha256'],
    candidateSetupSha256=final['candidateSetupSha256'],
    candidateStatus='bounded-validation-complete', publicationStatus='awaiting-publisher',
    scope='Limited VM validation and bounded source/dependency reuse. Not a claim that every feature, format, performance target or physical desktop journey passed.',
    directR4=dict(nativeContracts=native, actualHealthyPublic42Baseline=baseline,
                 actualUpgrade=final,
                 nativeReviewSha256=sha(BASE / 'r4-native-regressions/results/independent-review.json'),
                 baselineReviewSha256=sha(OUT / 'baseline-independent-review.json'),
                 upgradeReviewSha256=sha(OUT / 'final-journey-independent-review.json')),
    sourceReuseReview='r4-source-reuse-review.json',
    sourceReuseReviewSha256=sha(DEST / 'r4-source-reuse-review.json'),
    historicalR3SnapshotSha256=prior_sha, historicalR3=prior,
    historicalR3ReadonlyTiming=dict(reviewSha256=sha(timing_path), evidence=timing),
    validationRecord='../../Nook0.1.45验证记录.md',
    limitations=[
        'R3 38 actual named stages and59 bounded historical R1 reviews remain their original identities; not R4 re-execution.',
        'MP4 resume700ms, conversion first-query20s, capture analysis55ms original timing gates remain unpassed.',
        'OCR plan/layout/context checks are not actual OCR or online translation accuracy and not a WeChat comparison.',
        'Missing real Office engines and one privileged symlink test remain explicit limitations.',
        'No physical desktop/browser acceptance, every-format coverage, every canvas gesture or long-term memory benchmark.',
        'R3 installation1304 and original helper/readiness failures are preserved; R4 actual installation is a separate new outcome.'
    ])
prior_path.write_text(clean(public), encoding='utf-8')

md_path = REPO / 'docs/Nook0.1.45验证记录.md'
old = md_path.read_text(encoding='utf-8-sig')
assert '当前候选为 R4：已编译，最终安装及升级验收待测，尚未取得发布资格。' in old
(BASE / 'r4/validation-before-final.md').write_bytes(md_path.read_bytes())
now = datetime.datetime.now(datetime.timezone(datetime.timedelta(hours=8))).date().isoformat()
new = old.replace('2026-11-13。**当前候选为 R4：已编译，最终安装及升级验收待测，尚未取得发布资格。**',
    f'2026-11-13 开始验证，{now} 完成 R4 最终验收。**R4 已通过专用虚拟机的真实 .42 → .45 升级、旧程序删除、固定入口重开与生成数据保留核对。**')
new = new.replace('受影响维护、安全和实际升级须另测；其余结果只能在源码及调用范围复核后作为有界历史证据。',
    'R4 维护与安装安全补测共 522 条检查通过，真实升级另行通过；其余结果只作为经过源码、调用范围及依赖复核的历史证据，不计作 R4 重跑。')
new = new.replace('**R3 的 1304 升级失败仍然保留，R4 修复是否有效待真实升级验证。**',
    '**R3 的 1304 升级失败仍然保留。R4 在另一条新建健康 .42 基线上的实际升级通过，未通过延长生产等待上限或删除安全断言取得结果。**')
new = new.replace('**R3 安装验收未通过（`installation-not-qualified`），保留失败；R4 验收待测。**',
    '**R4 实际升级的限定验收已通过；R3 安装验收仍记为失败（`installation-not-qualified`）。**')
new = new.replace('## 实际安装与数据保留', '## R3 实际安装与数据保留（失败保留）', 1)
seconds = final['installationElapsedSeconds']
section = f'''## R4 新增的实际验收

- **受影响代码回归。** 1351 完成维护与更新产物两组 102 条检查；1353 完成安装安全 420 条检查。使用精确 R4 主程序，正常退出，所属子进程归零。它们是原生合约检查，真实安装单独记录。
- **重新建立健康 .42 基线。** 1375 先将失败现场移入单独保全目录，没有删除原文件；1376 实际安装公开 .42。1377 由该版本自己的设置界面完成浏览器「安装 / 修复」并正常关闭设置和主程序，1378 的生产健康快照与 1379 的独立清单一致。无实际 Chrome 收藏声明。
- **真实更新和旧程序清理。** 1381–1383 通过公开 .42 实际模型写入一个素材、一个单图画布、一个未显示贴图及非默认保存目录，并从固定入口启动、正常关闭及重开核对。1384 使用冻结 R4 安装器完成四步实际向导，新候选启动后旧 .42 目录确实删除，产品清理状态为 complete，正常退出；该 VM 中完整安装阶段约 {seconds:.1f} 秒，不能当作实体机安装速度。
- **更新后重新打开与数据检查。** 1385 从同一个固定入口启动精确 R4 并正常关闭；1386 核对素材、分类关系、画布、贴图、设置、保存路径和原图/资源字节；1387 独立清单核对旧目录已不存在、注册和快捷入口一致，测试程序全部退出。生成夹具不代表所有真实图库或全部绘图工具。

R4 安装器 SHA-256：`{final['candidateSetupSha256']}`。最终独立升级审核摘要 SHA-256：`{sha(OUT / 'final-journey-independent-review.json')}`。原 R3 失败没有改绿；首次 R4 测试准备读取启动回执过早，尚未执行产品阶段即停止，后续只修正测试准备等待并保留原记录。测试使用的显示保持状态均在阶段结束后主动正常释放。

[R3 → R4 有限复用审阅](validation/0.1.45/r4-source-reuse-review.json)核对 422 个封存输入，其中 420 个不变；仅清理阶段日志及真实目录扫描进度发生变化。打包的浏览器、转换组件和根目录依赖保持一致。R3 的 38 个实测命名阶段与 59 个历史审阅仍按原身份展示，不能合并宣传为 97 个 R4 实测阶段。

'''
new = new.replace('## R3 实际安装与数据保留（失败保留）', section + '## R3 实际安装与数据保留（失败保留）', 1)
new = new.replace('该失败保留，不视为完整升级通过', '该 R3 失败保留，不视为完整升级通过')
assert 'R4 验收待测' not in new and 'R4 修复是否有效待真实升级验证' not in new
md_path.write_text(new, encoding='utf-8')
print(json.dumps(dict(evidenceSummarySha256=sha(prior_path), validationRecordSha256=sha(md_path)), ensure_ascii=False))
