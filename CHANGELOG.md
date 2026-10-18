# Changelog

## [1.5.0](https://github.com/Atypical-Consulting/kaldor/compare/v1.4.0...v1.5.0) (2026-10-18)


### Added

* confirm "after" in rules.json lets unattended commits go to review ([#42](https://github.com/Atypical-Consulting/kaldor/issues/42)) ([126537a](https://github.com/Atypical-Consulting/kaldor/commit/126537aabff34cff20ad442b04a0ea514da3ead2))
* confirm several intents up front ([#40](https://github.com/Atypical-Consulting/kaldor/issues/40)) ([#49](https://github.com/Atypical-Consulting/kaldor/issues/49)) ([dc5704c](https://github.com/Atypical-Consulting/kaldor/commit/dc5704cb091d485c303478371a2472b540a487bb))
* one confirm --all serves every worktree of the repository ([#51](https://github.com/Atypical-Consulting/kaldor/issues/51)) ([#55](https://github.com/Atypical-Consulting/kaldor/issues/55)) ([e34a0cb](https://github.com/Atypical-Consulting/kaldor/commit/e34a0cb54af9496b5aa3ab4acce2fcfa5fc9d833))


### Fixed

* **gate:** keep Kaldor on when the agent leaves the branch with the rules ([#57](https://github.com/Atypical-Consulting/kaldor/issues/57)) ([442a12e](https://github.com/Atypical-Consulting/kaldor/commit/442a12e322af3acd496f2a67eff2047913b565d9))
* **gate:** read Kaldor's rules from HEAD; a .kaldor/ change needs approve ([#46](https://github.com/Atypical-Consulting/kaldor/issues/46)) ([ab55af3](https://github.com/Atypical-Consulting/kaldor/commit/ab55af357416c5c239b3bb1641f1445577ddc270))
* **gate:** refuse commits that skipped git commit ([#45](https://github.com/Atypical-Consulting/kaldor/issues/45)) ([#52](https://github.com/Atypical-Consulting/kaldor/issues/52)) ([66aa0fe](https://github.com/Atypical-Consulting/kaldor/commit/66aa0fe34dce3ad2d09cba6f7cbd9d12342a0e0f))
* **intent:** refuse human-only commands inside a Claude Code session ([#54](https://github.com/Atypical-Consulting/kaldor/issues/54)) ([76f468f](https://github.com/Atypical-Consulting/kaldor/commit/76f468fcf7b898b473d936c71b334bd0c43172ef))
* share the reviewed set across worktrees and check every branch on push ([#53](https://github.com/Atypical-Consulting/kaldor/issues/53)) ([#56](https://github.com/Atypical-Consulting/kaldor/issues/56)) ([9ba7de4](https://github.com/Atypical-Consulting/kaldor/commit/9ba7de4da1d09d95f781916f37540e8b5199fda4))

## [1.4.0](https://github.com/Atypical-Consulting/kaldor/compare/v1.3.0...v1.4.0) (2026-10-18)


### Added

* kaldor doctor says when a newer Kaldor is published ([#38](https://github.com/Atypical-Consulting/kaldor/issues/38)) ([c4634dd](https://github.com/Atypical-Consulting/kaldor/commit/c4634dd0c5be8ac7e3f3db7e53423e4ecda3aeb5))


### Changed

* simplify the codebase without changing behavior ([#39](https://github.com/Atypical-Consulting/kaldor/issues/39)) ([5bef6b7](https://github.com/Atypical-Consulting/kaldor/commit/5bef6b7e7831eceb19f55a24038bf27cba594c94))


### Documentation

* explain how a Kaldor intent differs from plan mode ([#34](https://github.com/Atypical-Consulting/kaldor/issues/34)) ([b045c08](https://github.com/Atypical-Consulting/kaldor/commit/b045c08b13f24e17ef53209be7bf56c7d8943b62))
* point the site's step 6 to the log in .git/kaldor/ ([#36](https://github.com/Atypical-Consulting/kaldor/issues/36)) ([c0d169c](https://github.com/Atypical-Consulting/kaldor/commit/c0d169cad9ed0efdc8479366028cc55812569fd6))

## [1.3.0](https://github.com/Atypical-Consulting/kaldor/compare/v1.2.1...v1.3.0) (2026-10-18)


### Added

* add kaldor intent wait so the agent notices the user's confirmation ([#32](https://github.com/Atypical-Consulting/kaldor/issues/32)) ([58be362](https://github.com/Atypical-Consulting/kaldor/commit/58be362a2ca70b2ba594eb2ac237368575ff4961))

## [1.2.1](https://github.com/Atypical-Consulting/kaldor/compare/v1.2.0...v1.2.1) (2026-10-18)


### Fixed

* give the user commands they can run, and refuse without a terminal first ([#30](https://github.com/Atypical-Consulting/kaldor/issues/30)) ([bc594dc](https://github.com/Atypical-Consulting/kaldor/commit/bc594dcb40a0820021a6a34217abf689e3e48b13))
* tell the user to commit the .kaldor/ setup themselves ([#27](https://github.com/Atypical-Consulting/kaldor/issues/27)) ([4712302](https://github.com/Atypical-Consulting/kaldor/commit/47123026364c7d6a1a3ee559b364e78ca92b0731))

## [1.2.0](https://github.com/Atypical-Consulting/kaldor/compare/v1.1.1...v1.2.0) (2026-10-18)


### Added

* commits wait for the user to confirm the frozen intent ([#25](https://github.com/Atypical-Consulting/kaldor/issues/25)) ([578f6bf](https://github.com/Atypical-Consulting/kaldor/commit/578f6bf4eaab586254b63e24650c3482d06e839e))

## [1.1.1](https://github.com/Atypical-Consulting/kaldor/compare/v1.1.0...v1.1.1) (2026-10-17)


### Fixed

* bring the pre-commit hook to the running version ([#24](https://github.com/Atypical-Consulting/kaldor/issues/24)) ([48b8b35](https://github.com/Atypical-Consulting/kaldor/commit/48b8b355e273d8d3eb9127335e0316b6f3d72612))


### Documentation

* bring the README and the site up to date with 1.1.0 ([#21](https://github.com/Atypical-Consulting/kaldor/issues/21)) ([41ab47a](https://github.com/Atypical-Consulting/kaldor/commit/41ab47a79de606f6ddebe4b2574dd9f1b6f3fc93))

## [1.1.0](https://github.com/Atypical-Consulting/kaldor/compare/v1.0.2...v1.1.0) (2026-10-17)


### Added

* keep state and history in the git directory and refuse shell writes to them ([#16](https://github.com/Atypical-Consulting/kaldor/issues/16)) ([244b86c](https://github.com/Atypical-Consulting/kaldor/commit/244b86c49404f2c8cd329e3707d0d7dbe7aa4319))


### Fixed

* align the doctor's plugin install hint with the docs ([#14](https://github.com/Atypical-Consulting/kaldor/issues/14)) ([b8228f1](https://github.com/Atypical-Consulting/kaldor/commit/b8228f193898748fab9c3660eccdc8fb706f91e0))

## [1.0.2](https://github.com/Atypical-Consulting/kaldor/compare/v1.0.1...v1.0.2) (2026-10-17)


### Fixed

* refuse human-only commands only when they actually run ([#12](https://github.com/Atypical-Consulting/kaldor/issues/12)) ([b579766](https://github.com/Atypical-Consulting/kaldor/commit/b5797663abe459e856a3e14698d84e364889dca2))

## [1.0.1](https://github.com/Atypical-Consulting/kaldor/compare/v1.0.0...v1.0.1) (2026-10-17)


### Fixed

* add a version command reachable through dnx and pin launcher line endings ([#4](https://github.com/Atypical-Consulting/kaldor/issues/4)) ([4a534d9](https://github.com/Atypical-Consulting/kaldor/commit/4a534d92633dd821a040712b6be5d84f2ccd5606))


### Documentation

* add the documentation site and its GitHub Pages deployment ([#6](https://github.com/Atypical-Consulting/kaldor/issues/6)) ([eed8627](https://github.com/Atypical-Consulting/kaldor/commit/eed8627de0bf810e44b623f0c8306ce76a019914))

## 1.0.0 (2026-10-17)


### Added

* implement Kaldor v1 ([#2](https://github.com/Atypical-Consulting/kaldor/issues/2)) ([1cedc90](https://github.com/Atypical-Consulting/kaldor/commit/1cedc904146908b157fe882267d105c487a7069f))


### Documentation

* dossier de projet et analyse d'architecture de Kaldor ([d3cb32c](https://github.com/Atypical-Consulting/kaldor/commit/d3cb32c6d8183700df2b9604bfcc16e28f27558e))
* translate repository content to English ([#1](https://github.com/Atypical-Consulting/kaldor/issues/1)) ([8b65e24](https://github.com/Atypical-Consulting/kaldor/commit/8b65e248a4f2623227433ce07986ddd2f00cf1c3))
