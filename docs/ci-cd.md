# Learn CI and image publishing

Stage 2 uses the existing [workflow](../.github/workflows/ecommerce.yml) and
[Dockerfile](../Dockerfile). Commands run from the repository root.

## Follow the workflow

```text
Push or pull request
  -> verify: frontend/backend build and tests
  -> verify: Docker stack, HTTP/browser checks, dependency recovery

Successful push to the default branch
  -> publish: log in to GHCR
  -> publish: build the verified source revision
  -> publish: push an image tagged with its full commit SHA
```

CI checks whether a change passes automated verification. Image publishing
packages that revision for later deployment. Continuous deployment will be a
separate step once a target environment exists.

## Read the important settings

```yaml
needs: verify
if: github.event_name == 'push' && github.ref == format('refs/heads/{0}', github.event.repository.default_branch)
permissions:
  contents: read
  packages: write
```

- `needs: verify`: publication waits for verification to succeed. A failed or
  cancelled verification job prevents publication.
- `if`: only pushes to the repository's default branch publish. Pull requests
  and pushes to other branches still run verification.
- `contents: read`: allows checkout. The verification job has no package-write
  permission.
- `packages: write`: gives the publication job permission to upload to GHCR.

GitHub supplies `GITHUB_TOKEN` for the run; no personal access token is needed
for this publishing workflow. The login command reads it through standard input.
Repository or organization policies must permit Actions and package creation.
For an existing package, its Actions access settings must permit this repository
to write to it.

The repository name is converted to lowercase, as required for Docker image
names. For this repository, the result is:

```text
ghcr.io/turingzhi/ecommerce:sha-<full-commit-sha>
```

The SHA identifies the source revision. The Dockerfile builds the React files,
publishes the .NET API, and combines both into the runtime image. The publication
job builds again from the verified revision; it does not reuse the exact image
tested in `verify`. Base image tags and restore inputs can change between builds.
Promoting the exact tested image by digest is a later improvement.

## Exercise 1: inspect an existing run

Open the repository's Actions tab and select the Ecommerce workflow. Find the
frontend checks, backend tests, Docker startup, and an outage/recovery check.
Explain what each proves. A successful build alone does not prove checkout or
dependency recovery works.

## Exercise 2: verify publication gating

In a disposable branch, temporarily change a unit-test expectation to be wrong,
then push the branch or open a pull request. Observe the failed verification and
skipped publication. Restore the expectation and observe verification succeeding;
publication should still be skipped because this is not a default-branch push.
Do not merge the deliberately failing test.

After merging the workflow change into the default branch, check that `verify`
succeeds before `publish` starts. Read the published image reference in the run's
summary. GitHub's Packages page should show the corresponding version.

## Exercise 3: pull the published image

Use the actual full SHA from the workflow summary:

```sh
docker pull ghcr.io/turingzhi/ecommerce:sha-<full-commit-sha>
docker image inspect ghcr.io/turingzhi/ecommerce:sha-<full-commit-sha>
```

Replace the angle-bracket placeholder before running these commands. Inspect the
`org.opencontainers.image.revision` label and compare it with the commit. Record
the registry digest shown after publication or pulling: a digest identifies the
image content, while a tag can be reassigned.

New GHCR packages are private by default. Local pulls of a private package need
an authenticated account with package access; follow GitHub's registry guidance
below. Publishing does not make the application accessible on a web server.

The publishing runner produces a Linux AMD64 image. Docker on Apple Silicon may
need emulation to run it. Multi-platform images are a later exercise.

## Completion checklist

- Explain why `publish` depends on `verify`.
- Demonstrate that a pull request cannot publish an image.
- Find a successful default-branch publication and its commit-tagged image.
- Pull that image and match its revision label to the commit.
- Explain the difference between CI, image publishing, and deployment.

The next stage adds a deployment target, configuration, health checks, and an
application rollback procedure. Database migrations require their own strategy.

## Official references

- [Publishing Docker images](https://docs.github.com/en/actions/tutorials/publish-packages/publish-docker-images)
- [Using jobs in a workflow](https://docs.github.com/en/actions/how-tos/write-workflows/choose-what-workflows-do/use-jobs)
- [Working with the Container registry](https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-container-registry)
