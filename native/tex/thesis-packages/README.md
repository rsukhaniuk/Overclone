# Additive thesis packages

`packages.lock.json` pins 13 TeX Live 2023 packages, their 34 original runtime,
source and documentation archives, and 94 staged resources. The archive inventory
is the final historic 2023 repository. Every selected runtime file and original
notice is copied byte for byte. No accepted P13 resource is replaced. The
corresponding source and documentation archives must accompany the dependency
records used to prepare release materials; staging is not release clearance.

Acquire the exact archive URLs in the lock into a separate directory, then run:

```sh
python3 native/tex/thesis-packages/prepare.py \
  --archives /inputs/thesis-archives --base /inputs/profile13 \
  --out /outputs/thesis-overlay
```

The command verifies all archives before creating a fresh output directory. It
copies only locked members and never extracts arbitrary archive paths. It refuses
mixed base inventories, collisions, symlinks, modified archives and missing
members. It does not fetch dependencies, install fonts or select an application
default. The overlay is an input to a separately closed runtime profile.

This closes the static package gaps found in the public KPI thesis pinned by
`tests/CompilerCompatibilityFixtures/public-kpi-gap.json`; actual compilation may
reveal more dependencies. Its requested Times New Roman and Courier New are
separate font dependencies. No Microsoft font or installer is included here.
Package staging alone does not prove compilation of that thesis.
