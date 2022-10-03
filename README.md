<p align="center">
<img src="https://img.icons8.com/flat-round/64/000000/rubber-ducky.png" alt="Ducktown logo">
</p>

<h1 align="center">The Opinionated .NET Compiler</h1>

# Scope

The fork removes a subset of functionalities that I consider are not useful in a composition-over-inheritence development practice

## Abstract

Abstract modifier opens up the system to unwanted inheritence for sharing code instead of describing relations between domain objects.
Instead of relying on the abstract modifier for code sharing consider doing composition and rely on interfaces to define a shared contract between components

### Changes

- [ ] Class modifier `abstract` is removed
- [ ] Method/Property modifier `abstract` is removed
- [ ] Interface method modifier `abstract` is removed

# Documentation

Visit [Roslyn Architecture Overview](https://docs.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/compiler-api-model) to get started with Roslyn’s API’s.

---

Icon from <a href="https://icons8.com/">@icons8</a>
