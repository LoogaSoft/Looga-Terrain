# Barrier Runs

Barrier runs place walls and fences along splines on the terrain.

## Set Up

1. Create a barrier set: Create > LoogaSoft > Terrain > Barrier Set.
2. Add the piece prefabs. Each piece has a weight. The run selects one piece for each segment by weight.
3. Optional: assign a post. The run places it at each joint and at both run ends. Leave it empty when the pieces
   include their own posts.
4. Set the piece length (joint spacing at scale 1), the scale, and the pivot offsets.
5. Add a SplineContainer and a `LoogaBarrierRun` to a GameObject. Assign the set and draw the spline.

Piece models must use +Z as the run direction. All pieces of one set must have the same length.

## Settings

| Setting | Effect |
|---|---|
| Piece Offset | Pivot position of a piece relative to the segment centre on the ground (X right, Y up, Z along). |
| Post Offset | Pivot position of a post relative to its joint on the ground. |
| Slope Mode | Step keeps each piece level at the lower end height. Follow tilts each piece, up to Maximum Pitch. |
| Stretch To Fit | Scales pieces along the run so the joints meet the spline exactly. Off: the run ends early. |
| Yaw Jitter | Random turn of each piece around the vertical axis. |
| Gate | Gate model for the gates of a run. Empty: a gate is an opening of Gate Width. |
| Gate Width | Distance between the two gate posts at scale 1. For a gate of two pieces, use two times the piece length. |
| Gate Offset | Pivot position of the gate relative to the gate centre on the ground. |
| Gate Has Posts | The gate model has its own posts. The run places no set post at the gate edges. |
| Gate Open Angle | Leaf turn of an open gate. Positive turns the leaves toward the right (+X) side of the gate. |
| Colliders | None, Box (one box for each piece), or Mesh (keeps holes in a piece). |

Step is the recommended mode for upright panels and fences. Follow tilts the whole piece, so vertical parts of the
piece lean against vertical posts on steep slopes.

## Run Settings

| Setting | Effect |
|---|---|
| Seed | Seed of the piece selection and the jitter. |
| Flip | Turns all pieces and gates 180 degrees. |
| Auto Rebuild | Builds again after a spline edit or a terrain change. |
| Joints At Knots | Places a joint and a post at every knot. The run fits the pieces of each curve separately. Use it for sharp corners, so that no piece spans a corner. |
| Ground | Terrain: the pieces stand on the terrain. Spline: the pieces stand at the spline height, for example on rock or on a structure above the terrain. |
| Gates | The gates of the run. See Gates. |

## Sections And Openings

Each spline of the container is one section. Add a spline to start a separate section. The space between two
sections is an opening, for example for a road.

## Gates

Add gates to the Gates list of the run. Each gate has a spline index, a position (0 = spline start, 1 = spline end),
a kind and a width. You can also drag the gate handle in the Scene view. The handle moves the gate to the nearest
point of its spline.

| Kind | Effect |
|---|---|
| Closed | The gate model of the set, with closed leaves. |
| Open | The gate model of the set, with leaves turned by the open angle. |
| Opening | A gap without a gate model, for example where a road passes. Width sets its length along the spline (0 = the set gate width). |

- The gate replaces the pieces that it covers. The pieces on each side stop at a post at the gate edge.
- No post stands in the gate. Two gates that touch share one post.
- The run fits the pieces between the gates and the spline ends separately, so Stretch To Fit applies to each part.
- A gate model is straight. On a curve, its two posts are Gate Width apart in a straight line. An opening follows
  the spline, also around corners.
- Set posts stand at both sides of an opening.
- The gate stays level at the lower post, like a piece in Step mode, also in Follow mode. On a slope, each leaf of a
  `LoogaBarrierGate` steps up to the lower ground of its own half: the hinge or the gate centre.
- Flip also turns the gates, so it changes the side to which an open gate opens.
- A gate that does not fit on its spline, or that overlaps another gate, is skipped. The inspector shows a warning.

### Gate Models

The gate pivot is at the gate centre on the ground at scale 1, and +Z is the run direction. Usually the posts come
from the set, and the gate model contains only the leaves and any frame between the posts. If the gate model has its
own posts, turn on Gate Has Posts in the set.

For leaves that can open, add a `LoogaBarrierGate` component to the gate root and assign the hinges:

- Each leaf is a child of a hinge object at a gate edge.
- The run finds the side of each leaf from the meshes below its hinge. A hinge without meshes uses its own +Z axis.
- For an open gate, the run turns each hinge around its up axis by the open angle of the set. All leaves open to the
  same side of the gate.

A model that uses another axis as the run direction needs a wrapper prefab. The wrapper root uses +Z as the run
direction, and the model is a turned child of the root.

With Box colliders, each hinge gets its own box, so the box turns with an open leaf.

## Instanced Rendering

Add one `LoogaBarrierInstancing` component to the scene. It draws the generated pieces of all barrier runs through
Looga Instancing (a `SceneInstanceProvider` on the same object). The pieces keep their colliders, and pieces of the
same model share one draw population.

- While a run builds again, all pieces draw natively. When no run has changed for Settle Seconds, the component
  registers the pieces again.
- Registration takes about one second for 4000 pieces. It also runs when the scene loads.
- Max Distance limits the draw distance of the pieces.

## Rebuilds

The generated pieces are saved with the scene and keep their prefab link. A run builds again after:

- a spline edit,
- an inspector edit of the run or of its set,
- a stamp-world terrain change in the area of the run.

Loading a scene does not rebuild the runs. Use Rebuild in the inspector after other changes, for example a change to
a piece prefab size.
