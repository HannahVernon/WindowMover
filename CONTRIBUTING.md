# Contributing to WindowMover

Thank you for your interest in contributing to WindowMover!

## Code of Conduct

This project follows the [Contributor Covenant Code of Conduct](CODE_OF_CONDUCT.md). By participating, you agree to uphold this code.

## Getting Started

### Prerequisites

- Windows 10 or later
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Visual Studio 2022 or later (recommended) with the .NET desktop development workload

### Building

1. Clone the repository and switch to the `dev` branch:

   ```
   git clone https://github.com/HannahVernon/WindowMover.git
   cd WindowMover
   git switch dev
   ```

2. Build the solution:

   ```
   dotnet build
   ```

### Running

```
dotnet run --project src\WindowMover
```

## How to Contribute

### Reporting Bugs

Open a [bug report](https://github.com/HannahVernon/WindowMover/issues/new?template=bug_report.yml) with steps to reproduce, expected vs. actual behavior, and your OS and monitor setup.

### Suggesting Features

Open a [feature request](https://github.com/HannahVernon/WindowMover/issues/new?template=feature_request.yml) describing the problem and your proposed solution.

### Pull Requests

1. Fork the repository and create a branch from `dev`:

   ```
   git switch -c feature/your-feature dev
   ```

2. Make your changes, keeping commits focused.

3. Ensure the project builds with zero warnings:

   ```
   dotnet build
   ```

4. Push your branch and open a pull request targeting `dev`.

## Branching Model

- `dev` is the integration branch for ongoing work.
- `main` contains stable releases.
- Create `feature/` or `fix/` branches from `dev`.

## License

This project is licensed under the [MIT License](LICENSE). By contributing, you agree that your contributions will be licensed under the same terms.