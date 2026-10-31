# Running declarad in CI/CD

A GitOps workflow: changes to AD are pull requests; CI shows the plan, and a merge applies it.

## Requirements

- A shared [state backend](../configuration/backends.md) with locking (S3, Azure Blob, HTTP/GitLab). Local state does not work across runners.
- A service account with delegated rights; its password in the CI secret store (`DECLARAD_PASSWORD`).
- Network access from the runner to a DC on LDAPS.

## Pipeline stages

```sh
# every pull request
declarad init --backend-config "key=ad/${ENVIRONMENT}.tfstate"
declarad fmt --check -r
declarad validate
declarad plan --no-color --lock-timeout 5m | tee plan.txt     # post plan.txt as a PR comment

# after merge to main
declarad init --backend-config "key=ad/${ENVIRONMENT}.tfstate"
declarad apply --auto-approve --no-color --lock-timeout 5m
```

`apply` plans again at apply time. If AD changed between review and merge, the applied changes reflect the current state.

## Drift detection

Schedule a nightly job:

```sh
declarad plan --detailed-exitcode --no-color
# exit 0: no drift; 2: drift (alert); 1: error
```

## GitHub Actions example

```yaml
name: active-directory
on:
  pull_request:
  push:
    branches: [main]
jobs:
  declarad:
    runs-on: [self-hosted, corp-network]
    env:
      DECLARAD_PASSWORD: ${{ secrets.DECLARAD_PASSWORD }}
      AWS_ROLE_ARN: ${{ vars.STATE_ROLE }}
    steps:
      - uses: actions/checkout@v4
      - run: declarad init
      - run: declarad fmt --check -r
      - run: declarad validate
      - run: declarad plan --no-color --lock-timeout 5m
        if: github.event_name == 'pull_request'
      - run: declarad apply --auto-approve --no-color --lock-timeout 5m
        if: github.ref == 'refs/heads/main'
```

## GitLab CI with GitLab-managed state

```yaml
variables:
  TF_HTTP_ADDRESS: "${CI_API_V4_URL}/projects/${CI_PROJECT_ID}/terraform/state/ad"
  TF_HTTP_LOCK_ADDRESS: "${TF_HTTP_ADDRESS}/lock"
  TF_HTTP_UNLOCK_ADDRESS: "${TF_HTTP_ADDRESS}/lock"
  TF_HTTP_USERNAME: gitlab-ci-token
  TF_HTTP_PASSWORD: "${CI_JOB_TOKEN}"

plan:
  script:
    - declarad init
    - declarad plan --no-color
apply:
  script:
    - declarad init
    - declarad apply --auto-approve --no-color
  rules:
    - if: $CI_COMMIT_BRANCH == $CI_DEFAULT_BRANCH
```

With `declarad { backend "http" { lock_method = "POST" unlock_method = "DELETE" } }` in the configuration.

## One configuration directory per environment

```sh
declarad -chdir=environments/prod init
declarad -chdir=environments/prod plan --detailed-exitcode
```

## Tips

- Pin module versions (`?ref=v1.2.0`) and run `declarad init` in every job.
- Keep environment differences in variables (`DECLARAD_VAR_environment`) or separate backends, not separate copies of the configuration.
- `--target` is for emergencies, not pipelines.
