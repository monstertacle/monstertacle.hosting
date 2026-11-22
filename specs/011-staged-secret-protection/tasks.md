# Implementation tasks

- [x] T001 Roll out SP-01 and SP-02 in monstertacle.hosting.
- [x] T002 Verify SP-03 and SP-04 for monstertacle.hosting.
- [x] T003 Roll out SP-01 and SP-02 in monstertacle.communication.
- [x] T004 Verify SP-03 and SP-04 for monstertacle.communication.
- [x] T005 Roll out SP-01 and SP-02 in monstertacle.multisession.
- [x] T006 Verify SP-03 and SP-04 for monstertacle.multisession.
- [x] T007 Roll out SP-01 and SP-02 in monstertacle.sharpqrs.
- [x] T008 Verify SP-03 and SP-04 for monstertacle.sharpqrs.

Both GitHub protections were verified on all four repositories. Each local
scanner accepted clean staged changes and an unstaged synthetic value,
rejected the staged synthetic value without printing it, and failed closed
when the dependency or index was invalid.

| Repository | Rollout PR | Passing implementation-head PR runs |
| --- | --- | --- |
| Hosting | [#10](https://github.com/monstertacle/monstertacle.hosting/pull/10) | [Build and test](https://github.com/monstertacle/monstertacle.hosting/actions/runs/45022481155) |
| Communication | [#3](https://github.com/monstertacle/monstertacle.communication/pull/3) | [Build and test](https://github.com/monstertacle/monstertacle.communication/actions/runs/45022926247) |
| Multisession | [#44](https://github.com/monstertacle/monstertacle.multisession/pull/44) | [Quality](https://github.com/monstertacle/monstertacle.multisession/actions/runs/45022807524), [Codemetr guard](https://github.com/monstertacle/monstertacle.multisession/actions/runs/45022807642) |
| SharpQrs | [#10](https://github.com/monstertacle/monstertacle.sharpqrs/pull/10) | [Verify](https://github.com/monstertacle/monstertacle.sharpqrs/actions/runs/45022926831) |
