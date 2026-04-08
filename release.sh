#!/bin/bash
# Bivium release script
# Usage: ./release.sh <tag> <notes>

set -e

if [ $# -lt 2 ]; then
    echo "Usage: ./release.sh <tag> <notes>"
    exit 1
fi

TAG="$1"
NOTES="$2"

PROJECT="Bivium/Bivium.csproj"
ARTIFACTS_DIR="release-artifacts"
PUBLISH_DIR="publish"
DOCKER_IMAGE="draknodd/bivium"
VERSION="${TAG#v}"

RIDS=("win-x64" "linux-x64" "linux-arm64" "osx-x64" "osx-arm64")

confirm_step() {
    read -r -p "$1 [Y/n] " choice
    if [ "$choice" = "n" ] || [ "$choice" = "N" ]; then
        echo "Aborted."
        exit 0
    fi
}

cleanup() {
    rm -rf "$PUBLISH_DIR" "$ARTIFACTS_DIR"
}

trap cleanup EXIT

# Clean previous builds
cleanup
mkdir -p "$ARTIFACTS_DIR"

# Restore client-side libraries (Monaco, WebTUI, etc.)
confirm_step "Restore client-side libraries?"
echo "Restoring client-side libraries..."
cd Bivium && libman restore && cd ..

# Build all targets
confirm_step "Build binaries for ${#RIDS[@]} platforms?"
for rid in "${RIDS[@]}"; do
    echo "Building $rid..."

    dotnet publish "$PROJECT" -c Release -r "$rid" --self-contained true \
        -p:PublishSingleFile=true \
        -p:PublishTrimmed=false \
        -p:EnableCompressionInSingleFile=true \
        -p:Version="$VERSION" \
        -o "$PUBLISH_DIR/$rid"

    cd "$PUBLISH_DIR/$rid"
    zip -r "../../$ARTIFACTS_DIR/Bivium-$rid.zip" .
    cd ../..

    echo "$rid done."
done

# Build Docker image
confirm_step "Build Docker image ${DOCKER_IMAGE}:${TAG}?"
echo "Building Docker image..."
docker build --build-arg VERSION="$VERSION" -t "${DOCKER_IMAGE}:${TAG}" -t "${DOCKER_IMAGE}:latest" .

# Push Docker image
confirm_step "Push Docker image to Docker Hub?"
echo "Pushing Docker image..."
docker push "${DOCKER_IMAGE}:${TAG}"
docker push "${DOCKER_IMAGE}:latest"
echo "Docker image pushed."

# Create and push tag
confirm_step "Create git tag $TAG and push?"
echo "Creating tag $TAG..."
if ! git tag "$TAG"; then
    echo "Failed to create tag (already exists?)"
    exit 1
fi
if ! git push origin "$TAG"; then
    echo "Failed to push tag, removing local tag..."
    git tag -d "$TAG"
    exit 1
fi

# Create GitHub release
confirm_step "Create GitHub release with artifacts?"
echo "Creating GitHub release..."
if ! gh release create "$TAG" "$ARTIFACTS_DIR"/*.zip --title "$TAG" --notes "$NOTES"; then
    echo "Failed to create release, removing tag..."
    git push origin --delete "$TAG"
    git tag -d "$TAG"
    exit 1
fi

echo "Release $TAG published successfully."
