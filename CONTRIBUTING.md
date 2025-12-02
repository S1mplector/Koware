# Contributing to Koware

Thank you for your interest in contributing to Koware! This document outlines guidelines for contributing to this project.

## License Notice

Koware is licensed under **Creative Commons Attribution-NoDerivatives 4.0 International (CC BY-ND 4.0)**. This means:

- ✅ You may view and study the source code
- ✅ You may submit contributions (issues, pull requests) to this repository
- ✅ Accepted contributions become part of the original project
- ❌ You may NOT distribute modified versions independently
- ❌ You may NOT create derivative works for redistribution

By submitting a contribution, you agree that your contribution will be licensed under the same terms and that the project maintainer has the right to include, modify, or reject your contribution.

## How to Contribute

### Reporting Bugs

1. **Search existing issues** to avoid duplicates
2. Use the **Bug Report** template
3. Include:
   - Clear description of the issue
   - Steps to reproduce
   - Expected vs actual behavior
   - Your environment (OS, .NET version)
   - Relevant logs or screenshots

### Suggesting Features

1. **Search existing issues** for similar suggestions
2. Use the **Feature Request** template
3. Describe:
   - The problem you're trying to solve
   - Your proposed solution
   - Alternative approaches you've considered

### Submitting Code Changes

#### Before You Start

1. **Open an issue first** to discuss significant changes
2. Fork the repository (for reference only - you cannot redistribute modifications)
3. Create a feature branch: `git checkout -b feature/your-feature-name`

#### Code Guidelines

- Follow existing code style and conventions
- Use meaningful commit messages
- Add XML documentation for public APIs
- Write tests for new functionality
- Keep changes focused and atomic

#### Pull Request Process

1. Ensure your code builds without errors
2. Run tests: `dotnet test Koware.Tests/Koware.Tests.csproj`
3. Update documentation if needed
4. Fill out the PR template completely
5. Wait for review

### Code Style

- **C#**: Follow [Microsoft's C# Coding Conventions](https://docs.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/coding-conventions)
- **Naming**: Use PascalCase for public members, camelCase for private fields
- **Documentation**: XML docs for all public types and members
- **File headers**: Include author attribution comment

Example file header:
```csharp
// Author: Your Name
// Summary: Brief description of the file's purpose.
```

## Development Setup

### Prerequisites

- .NET 8 SDK or later
- Git
- IDE: Visual Studio, VS Code, or Rider recommended

### Building

```bash
# Clone (for local development only)
git clone https://github.com/YourUsername/Koware.git
cd Koware

# Build
dotnet build

# Run tests
dotnet test Koware.Tests/Koware.Tests.csproj

# Run CLI
dotnet run --project Koware.Cli -- help
```

### Project Structure

```
Koware/
├── Koware.Cli/           # CLI application
├── Koware.Application/   # Use cases and orchestration
├── Koware.Domain/        # Domain models
├── Koware.Infrastructure/# External integrations, scrapers
├── Koware.Tests/         # Unit tests
└── Scripts/              # Build and packaging scripts
```

## What We're Looking For

### High Priority
- Bug fixes
- Performance improvements
- Cross-platform compatibility fixes
- Documentation improvements
- Test coverage

### Welcome Contributions
- New features (discuss first)
- Code cleanup and refactoring
- Accessibility improvements

### Please Avoid
- Changes to licensing or attribution
- Adding new external dependencies without discussion
- Large refactors without prior approval

## Getting Help

- **Questions**: Open a Discussion or Issue
- **Bugs**: Use the Bug Report template
- **Features**: Use the Feature Request template

## Code of Conduct

- Be respectful and constructive
- Focus on the code, not the person
- Welcome newcomers
- Accept feedback gracefully

---

Thank you for contributing to Koware!
