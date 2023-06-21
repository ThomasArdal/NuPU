# NuPU

NuGet Package Updater (NuPU) is an interactive CLI for updating NuGet packages.

## Installation

```console
dotnet tool install --global NuPU
```

## Usage

Run the `nupu` command in the root of your project to check all packages:

```console
nupu
```

Check all packages in a specific directory using the `directory` option:

```console
nupu --directory c:\projects\project-to-update
```

Check a single package for updates using the `package` option:

```console
nupu --package Spectre.Console
```

Get additional help using the `help` option:

```console
nupu --help
```

### Ignoring directories

Out of the box, NuPU ignores a list of directories to process like `bin`, `.git`, `packages`, and similar. You can override this behavior by creating a file named `.nupuignore` in a root folder you want to run the NuPU command in:

```
.git
.github
.vs
.vscode
bin
obj
packages
node_modules
my_custom_folder
```