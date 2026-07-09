# Domain Rules

墨声当前优先服务咨询、投行、PE/VC、战略、财务和高净值商务人群。专业词处理分两层：

- 本地规则：只处理高确定性错音、错字和口水词，随 EXE 一起发布，所有用户一致。
- LLM prompt：覆盖更宽的行业词表，让模型结合上下文判断，不做机械替换。

## Research Scope

本轮按真实业务语境整理词表，而不是只补零散错别字：

- 咨询方法：MECE、议题树、假设驱动、金字塔原理、市场规模测算、价值链、GTM、benchmark、桌面研究、专家访谈、管理层访谈。
- 投行交易：IPO、M&A、并购、卖方顾问、买方顾问、尽调、路演、信息备忘录、teaser、NDA、term sheet、SPA、交割、earn-out、协同效应。
- 估值建模：DCF、LBO、可比公司法、可比交易法、EV/EBITDA、P/E、P/B、WACC、终值、永续增长率、退出倍数、敏感性分析。
- 财务指标：收入、毛利率、EBITDA、EBIT、净利润、自由现金流、NWC、CapEx、OpEx。
- PE/VC：GP、LP、IRR、MOIC、DPI、TVPI、RVPI、capital call、carry、hurdle rate、dry powder、portfolio company、exit。
- 机构品牌：麦肯锡、贝恩、BCG、埃森哲、德勤、PwC、安永、KPMG、高盛、摩根士丹利、J.P. Morgan、花旗、瑞银、美银、巴克莱、Jefferies、Lazard、Evercore、中金公司、中信证券、中信建投、华泰证券、国泰君安、招商证券、招银国际。

## Local Corrections

本地只做“基本不会误伤”的规则，例如：

- 麦青锡 / 麦肯西 / 麦肯希 / 卖肯锡 -> 麦肯锡
- 贝因 / 贝音 -> 贝恩
- 必CG / 毕西基 / 比西基 / B C G -> BCG
- 高胜 -> 高盛
- 摩根斯坦利 / 摩根史丹利 / 摩根斯丹利 -> 摩根士丹利
- 中经公司 -> 中金公司
- 中信建头 -> 中信建投
- 并够 / 并勾 / 病购 -> 并购
- 境调 / 竞调 / 敬调 / 金调 -> 尽调
- 路言 / 路艳 -> 路演
- 估直 / 古直 / 古值 / 估纸 -> 估值
- 财务模形 / 财务磨型 -> 财务模型
- 杆杆收购 / 杠杆收购 -> LBO
- 首次公开发行 -> IPO
- 加权平均资本成本 -> WACC
- 莫伊克 / 毛伊克 -> MOIC
- 质朴 -> 智谱
- typus -> typos

## Seed Glossary

金融词库种子文件放在：

`MoshengNativeApp/resources/financial_glossary_seed.json`

结构如下：

```json
{
  "canonical": "港交所",
  "aliases": ["香港交易所", "HKEX"],
  "wrong_asr": ["港交索"],
  "category": "hong_kong_equity",
  "local_replace": true
}
```

- `canonical`：标准写法。
- `aliases`：同义词和常见英文写法，只进入 LLM prompt，不做本地硬替换。
- `wrong_asr`：ASR 常见错写；只有 `local_replace=true` 时才会本地替换。
- `category`：领域分类，用于 prompt 分组。
- `local_replace`：是否允许本地硬替换。默认要保守。

当前覆盖：

- 上市/资本市场：IPO、递表、聆讯、招股书、路演、簿记建档、基石投资者、绿鞋机制、承销、保荐人、全球发售、国际配售、增发、二次上市。
- 港股：港交所、港股、H 股、A 股、A+H、红筹、蓝筹、中概股、港股通、北向资金、南向资金、恒生指数、恒生科技指数。
- 美股：NYSE、Nasdaq、ADR、ADS、SPAC、de-SPAC、S-1、F-1、10-K、10-Q、8-K、PIPE、secondary offering。
- A 股：上交所、深交所、北交所、科创板、创业板、新三板、注册制、核准制、询价、打新、限售股、解禁、大宗交易、融资融券、沪股通、深股通、沪深 300、中证 500、中证 1000、北证 50。
- 交易/行情：市值、流通市值、成交量、成交额、换手率、买一、卖一、买卖价差、限价单、市价单、止损单、做空、卖空、保证金、追加保证金、停牌、复牌、涨停、跌停。
- 基金/ETF/REITs：ETF、REITs、公募基金、私募基金、货币基金、指数基金、QDII、QFII、LOF、FOF、NAV、AUM、管理费、托管费、申购、赎回。
- 财务指标：收入、毛利、毛利率、净利润、经调整净利润、EBITDA、EBIT、自由现金流、经营现金流、CapEx、OpEx、NWC、折旧、摊销、减值。
- 估值建模：DCF、WACC、终值、永续增长率、退出倍数、可比公司法、可比交易法、EV/Revenue、EV/EBITDA、P/E、P/B、P/S、PEG、敏感性分析、情景分析。
- PE/VC：GP、LP、IRR、MOIC、DPI、TVPI、RVPI、capital call、carry、hurdle rate、dry powder、portfolio company、exit、secondary、co-invest。
- 债券/可转债：票息、到期收益率、久期、凸性、信用利差、可转债、可交换债、优先股、永续债、高收益债、投资级债。
- 衍生品/风险：期权、期货、互换、远期、权证、牛熊证、Delta、Gamma、Vega、Theta、隐含波动率、行权价、名义本金、对冲、敞口。
- 报表科目：资产负债表、利润表、现金流量表、营业收入、营业成本、销售费用、管理费用、研发费用、应收账款、应付账款、存货、递延收入、商誉、无形资产、少数股东权益。

## Prompt Glossary

更广的专业词表放在 `BuildDomainGlossary()` 中，整理输入和邮件输入共用。prompt 明确要求：

- 只在语义明确时修正，不为了凑术语强行替换。
- 英文专业词不要强行翻译，也不要把正确中文名全部改成英文缩写。
- 保留用户的真实意图、数字、判断和不确定性，不编造事实。
- 对咨询/投行/PE/VC 上下文里的专业词优先按行业写法处理。

## Why Not All Local

很多词不能硬替换：

- “可比公司”可能是普通名词，也可能是“可比公司法”的简称。
- “carry”可能是基金收益分成，也可能是英文动词。
- “GP/LP”在基金语境下明确，但普通英文语境里可能不是基金概念。
- “大摩小摩”比强行替换成英文名更自然，取决于用户语境。

因此，本地规则只做高置信纠错；语义判断交给 LLM。

## Future

灰度阶段先把规则写进 EXE。后续可以把行业词表拆成 JSON 或远程配置，让测试用户不用更新 EXE 也能获得术语更新。
