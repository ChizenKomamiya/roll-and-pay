import { readFileSync } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { test } from 'node:test'
import assert from 'node:assert/strict'

const root = fileURLToPath(new URL('../../', import.meta.url))
const assets = ['frontend', 'backend', 'infrastructure', 'database']

// Read the actual block lists; actionlint separately validates YAML/Actions syntax.
function filters(asset, event) {
  const lines = readFileSync(path.join(root, '.github/workflows', `${asset}.yml`), 'utf8').split(/\r?\n/)
  const start = lines.indexOf(`  ${event}:`)
  assert.notEqual(start, -1, `${asset}: ${event} event missing`)
  let end = start + 1
  while (end < lines.length && !/^  [\w_]+:/.test(lines[end]) && !/^permissions:/.test(lines[end])) end++
  const patterns = lines.slice(start + 1, end).flatMap(line => /^      - '([^']+)'$/.exec(line)?.slice(1) ?? [])
  assert.ok(patterns.length, `${asset}: no explicit path filters`)
  return patterns
}
function triggered(asset, changedPath) {
  let included = false
  for (const pattern of filters(asset, 'push')) {
    const negative = pattern.startsWith('!')
    if (path.posix.matchesGlob(changedPath, negative ? pattern.slice(1) : pattern)) included = !negative
  }
  return included
}

const cases = [
  ['frontend/src/pages/home.ts', ['frontend']],
  ['frontend/package.json', ['frontend']],
  ['frontend/pnpm-lock.yaml', ['frontend']],
  ['backend/Functions/HealthFunction.cs', ['backend']],
  ['backend/global.json', ['backend']],
  ['backend/RollAndPay.Api.csproj', ['backend']],
  ['infra/resources.bicep', ['infrastructure']],
  ['infra/prod.bicepparam', ['infrastructure']],
  ['database/main.bicep', ['database']],
  ['database/Apply-Schema.ps1', ['database']],
  // Both Bicep entry points depend on this shared definition. Neither auto-applies.
  ['database/container-definition.json', ['infrastructure', 'database']],
  ['pipeline/frontend/Build.ps1', ['frontend']],
  ['pipeline/backend/Build.ps1', ['backend']],
  ['pipeline/infrastructure/Deploy.ps1', ['infrastructure']],
  ['pipeline/database/Validate.ps1', ['database']],
  ['.github/workflows/frontend.yml', ['frontend']],
  ['.github/workflows/backend.yml', ['backend']],
  ['.github/workflows/infrastructure.yml', ['infrastructure']],
  ['.github/workflows/database.yml', ['database']],
  ['.github/workflows/pipelines.yml', []],
  ['pipeline/tests/trigger-filters.test.mjs', []],
  ['README.md', []],
  ['frontend/README.md', []],
  ['backend/README.md', []],
  ['infra/README.md', []],
  ['infra/scripts/README.md', []],
  ['database/README.md', []],
  ['pipeline/README.md', []],
  ['docs/screens-and-features.md', []],
  ['spec/screen-mockups.md', []],
]
for (const [changedPath, expected] of cases) {
  test(`change ${changedPath}`, () => assert.deepEqual(assets.filter(asset => triggered(asset, changedPath)), expected))
}
for (const asset of assets) {
  test(`${asset}: PR and push filters agree`, () => assert.deepEqual(filters(asset, 'pull_request'), filters(asset, 'push')))
  test(`${asset}: automatic push is restricted to main`, () => {
    const content = readFileSync(path.join(root, '.github/workflows', `${asset}.yml`), 'utf8')
    assert.match(content, /push:\r?\n    branches: \[main\]/)
    if (asset === 'infrastructure' || asset === 'database') assert.match(content, /if: github.event_name == 'workflow_dispatch' && github.ref == 'refs\/heads\/main' && inputs.operation != 'validate'/)
  })
}
