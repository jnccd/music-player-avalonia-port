{
  description = "Nix Dev shell for Avalonia .NET Desktop/Android development";

  inputs.nixpkgs.url = "github:NixOS/nixpkgs/nixos-25.11";

  outputs = { self, nixpkgs }:
    let
      system = "x86_64-linux";
      pkgs = import nixpkgs {inherit system;};
    in {
      devShells.${system} = {
        default = with pkgs;
          mkShell {
            packages = [ icu dotnet-sdk_10 dotnet-ef ];
          }
      };
    };
}
