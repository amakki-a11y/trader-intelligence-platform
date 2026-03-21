import C from "../../styles/colors";
import Badge from "./Badge";

function RoutingBadge({ routing }: { routing: string }) {
  const colors: Record<string, string> = { "A-Book": C.red, "Review": C.amber, "B-Book": C.green };
  return <Badge color={colors[routing] ?? C.t3}>{routing}</Badge>;
}

export default RoutingBadge;
